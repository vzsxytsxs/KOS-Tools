// KOS Equalizer: an audio processing object (APO) the audio engine (audiodg.exe) loads as an endpoint effect. It runs a chain
// of biquad filters and a preamp over the endpoint's float samples. Its settings are a small text file per endpoint, written by
// KOS Equalizer and KOS Engine; a watcher thread reads it and swaps in new coefficients, so the audio thread never does I/O,
// never takes a lock and never allocates.
//
// audiodg is a privileged process: the settings file and its folders are refused when they're links or (the file and the Audio
// folders) not owned by SYSTEM or Administrators, the file is capped at 64 KB, and a curve boosting more than MaxBoost dB is
// refused (the previous settings stay). What was applied or refused goes to a status file in audiodg's TEMP folder
// (KosEqApo-<endpoint guid>.txt), which KOS Equalizer reads back.
//
// It implements the three APO interfaces itself (the SDK's base class needs ATL). Float samples only, the same format on both
// sides, processed in place.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <aclapi.h>
#include <initguid.h>
#include <mmdeviceapi.h>
#include <audioenginebaseapo.h>
#include <audiomediatype.h>
#include <ks.h>
#include <ksmedia.h>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cwchar>
#include <locale.h>
#include <new>

// Locked memory for real-time use (audioeng.dll; declared in the SDK's base APO header, which needs ATL)
extern "C" STDAPI AERT_Allocate(size_t size, void** memory);
extern "C" STDAPI AERT_Free(void* memory);

// {610C4861-EB61-417D-A653-45B03D493123}
DEFINE_GUID(CLSID_KosEqApo, 0x610c4861, 0xeb61, 0x417d, 0xa6, 0x53, 0x45, 0xb0, 0x3d, 0x49, 0x31, 0x23);

static std::atomic<long> g_objects{0}, g_locks{0};

namespace
{
constexpr int MaxBands = 64;
constexpr double Pi = 3.14159265358979323846;
constexpr double MaxBoost = 24;            // dB, preamp included; the same number is KosToolbox.Core.Audio.EqSettingsFile.MaxBoost
constexpr DWORD MaxFileBytes = 64 * 1024;

enum class FilterType { Peak, LowShelf, HighShelf, LowPass, HighPass, Notch, BandPass };

struct Band { FilterType type; double frequency, gain, q; };

struct Coefficients { double b0, b1, b2, a1, a2; };

// What the audio thread runs: built on the watcher thread, swapped in whole.
struct Chain
{
    double preamp = 1.0; // linear
    double preampDb = 0;
    int count = 0;
    Coefficients filters[MaxBands];
    unsigned generation = 0;
};

// Audio EQ Cookbook (R. Bristow-Johnson), the same formulas as KosToolbox.Core.Audio.Equalizer.Design.
Coefficients Design(const Band& band, double rate)
{
    double f = band.frequency < 1 ? 1 : band.frequency > 0.49 * rate ? 0.49 * rate : band.frequency;
    double q = band.q < 0.1 ? 0.1 : band.q > 30 ? 30 : band.q;
    double gain = band.gain < -30 ? -30 : band.gain > 30 ? 30 : band.gain;
    double w0 = 2 * Pi * f / rate, c = std::cos(w0), alpha = std::sin(w0) / (2 * q), a = std::pow(10.0, gain / 40), tsa = 2 * std::sqrt(a) * alpha;
    double b0, b1, b2, a0, a1, a2;
    switch (band.type)
    {
    case FilterType::LowShelf:
        b0 = a * ((a + 1) - (a - 1) * c + tsa); b1 = 2 * a * ((a - 1) - (a + 1) * c); b2 = a * ((a + 1) - (a - 1) * c - tsa);
        a0 = (a + 1) + (a - 1) * c + tsa; a1 = -2 * ((a - 1) + (a + 1) * c); a2 = (a + 1) + (a - 1) * c - tsa; break;
    case FilterType::HighShelf:
        b0 = a * ((a + 1) + (a - 1) * c + tsa); b1 = -2 * a * ((a - 1) + (a + 1) * c); b2 = a * ((a + 1) + (a - 1) * c - tsa);
        a0 = (a + 1) - (a - 1) * c + tsa; a1 = 2 * ((a - 1) - (a + 1) * c); a2 = (a + 1) - (a - 1) * c - tsa; break;
    case FilterType::LowPass: b0 = (1 - c) / 2; b1 = 1 - c; b2 = b0; a0 = 1 + alpha; a1 = -2 * c; a2 = 1 - alpha; break;
    case FilterType::HighPass: b0 = (1 + c) / 2; b1 = -(1 + c); b2 = b0; a0 = 1 + alpha; a1 = -2 * c; a2 = 1 - alpha; break;
    case FilterType::Notch: b0 = 1; b1 = -2 * c; b2 = 1; a0 = 1 + alpha; a1 = -2 * c; a2 = 1 - alpha; break;
    case FilterType::BandPass: b0 = alpha; b1 = 0; b2 = -alpha; a0 = 1 + alpha; a1 = -2 * c; a2 = 1 - alpha; break;
    default: b0 = 1 + alpha * a; b1 = -2 * c; b2 = 1 - alpha * a; a0 = 1 + alpha / a; a1 = -2 * c; a2 = 1 - alpha / a; break;
    }
    return { b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0 };
}

// The curve's highest point in dB, preamp included: 512 log-spaced points from 20 Hz to 20 kHz (as Equalizer.PeakGain).
double PeakDb(const Chain& chain, double rate)
{
    double peak = -1e9;
    for (int i = 0; i < 512; i++)
    {
        double f = 20 * std::pow(1000.0, i / 511.0);
        if (f > 0.49 * rate) break;
        double w = 2 * Pi * f / rate, c1 = std::cos(w), s1 = -std::sin(w), c2 = std::cos(2 * w), s2 = -std::sin(2 * w), db = chain.preampDb;
        for (int b = 0; b < chain.count; b++)
        {
            const Coefficients& k = chain.filters[b];
            double nr = k.b0 + k.b1 * c1 + k.b2 * c2, ni = k.b1 * s1 + k.b2 * s2;
            double dr = 1 + k.a1 * c1 + k.a2 * c2, di = k.a1 * s1 + k.a2 * s2;
            double power = (nr * nr + ni * ni) / (dr * dr + di * di);
            db += 10 * std::log10(power > 1e-24 ? power : 1e-24);
        }
        if (db > peak) peak = db;
    }
    return peak;
}

bool ParseType(const char* word, FilterType& type)
{
    static const struct { const char* name; FilterType type; } names[] = {
        { "peak", FilterType::Peak }, { "lowshelf", FilterType::LowShelf }, { "highshelf", FilterType::HighShelf },
        { "lowpass", FilterType::LowPass }, { "highpass", FilterType::HighPass }, { "notch", FilterType::Notch }, { "bandpass", FilterType::BandPass } };
    for (auto& n : names)
        if (_stricmp(word, n.name) == 0) { type = n.type; return true; }
    return false;
}

// One line: "preamp -3.5" or "peak 1000 6 1.41" (type, frequency Hz, gain dB, Q). Anything else is skipped; numbers use a
// decimal point.
void ParseLine(char* line, Chain& chain, double rate, _locale_t c)
{
    char word[32] = {};
    if (sscanf_s(line, "%31s", word, (unsigned)sizeof word) != 1 || word[0] == '#') return;
    char* p = strstr(line, word) + strlen(word); // past any leading spaces and the word
    char* end = nullptr;
    if (_stricmp(word, "preamp") == 0)
    {
        double db = _strtod_l(p, &end, c);
        if (end != p && db >= -30 && db <= 30) { chain.preampDb = db; chain.preamp = std::pow(10.0, db / 20); }
        return;
    }
    FilterType type;
    if (!ParseType(word, type) || chain.count >= MaxBands) return;
    double f = _strtod_l(p, &end, c); if (end == p) return; p = end;
    double g = _strtod_l(p, &end, c); if (end == p) return; p = end;
    double q = _strtod_l(p, &end, c); if (end == p) return;
    if (!(f >= 10 && f <= 24000) || !(g >= -30 && g <= 30) || !(q >= 0.1 && q <= 30)) return;
    chain.filters[chain.count++] = Design({ type, f, g, q }, rate);
}

bool OwnedByAdmins(HANDLE h)
{
    PSID owner = nullptr;
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (GetSecurityInfo(h, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION, &owner, nullptr, nullptr, nullptr, &sd) != ERROR_SUCCESS) return false;
    bool ok = owner && (IsWellKnownSid(owner, WinLocalSystemSid) || IsWellKnownSid(owner, WinBuiltinAdministratorsSid));
    LocalFree(sd);
    return ok;
}

enum class Load { Ok, Missing, Refused };

// Opens a file or folder on the settings path without following a link there; refuses a link, anything not on a disk and
// (owner) anything SYSTEM or Administrators don't own. Links further up the path are what the folder checks are for.
Load Open(const wchar_t* path, bool folder, bool owner, HANDLE& handle, const char*& problem)
{
    handle = CreateFileW(path, folder ? (FILE_READ_ATTRIBUTES | READ_CONTROL) : GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | (folder ? FILE_FLAG_BACKUP_SEMANTICS : 0), nullptr);
    if (handle == INVALID_HANDLE_VALUE)
    {
        handle = nullptr;
        DWORD e = GetLastError();
        if (e == ERROR_FILE_NOT_FOUND || e == ERROR_PATH_NOT_FOUND) { problem = "no settings file"; return Load::Missing; }
        problem = "the settings can't be opened";
        return Load::Refused;
    }
    BY_HANDLE_FILE_INFORMATION info = {};
    if (GetFileType(handle) != FILE_TYPE_DISK || !GetFileInformationByHandle(handle, &info)) problem = "the settings aren't a file on a disk";
    else if (info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) problem = "the settings path holds a link";
    else if (((info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) != folder) problem = "the settings path holds the wrong kind of item";
    else if (owner && !OwnedByAdmins(handle)) problem = "the settings aren't owned by SYSTEM or Administrators";
    else return Load::Ok;
    CloseHandle(handle);
    handle = nullptr;
    return Load::Refused;
}

// Reads %ProgramData%\TheKOS\Audio\Eq\<guid>.eq into the chain (designed for the rate).
Load ReadSettings(const wchar_t* guid, Chain& chain, double rate, const char*& problem)
{
    wchar_t data[MAX_PATH] = L"C:\\ProgramData", folders[3][MAX_PATH], path[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(L"ProgramData", data, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) wcscpy_s(data, L"C:\\ProgramData");
    swprintf_s(folders[0], L"%s\\TheKOS", data);
    swprintf_s(folders[1], L"%s\\TheKOS\\Audio", data);
    swprintf_s(folders[2], L"%s\\TheKOS\\Audio\\Eq", data);
    swprintf_s(path, L"%s\\TheKOS\\Audio\\Eq\\%s.eq", data, guid);
    HANDLE h = nullptr;
    for (int i = 0; i < 3; i++)
    {
        Load folder = Open(folders[i], true, i > 0, h, problem);
        if (folder != Load::Ok) return folder;
        CloseHandle(h);
    }
    Load file = Open(path, false, true, h, problem);
    if (file != Load::Ok) return file;
    LARGE_INTEGER size = {};
    char* text = nullptr;
    DWORD read = 0;
    Load result = Load::Refused;
    if (!GetFileSizeEx(h, &size) || size.QuadPart > MaxFileBytes) problem = "the settings file is over 64 KB";
    else if (!(text = new (std::nothrow) char[MaxFileBytes + 1])) problem = "out of memory";
    else if (!ReadFile(h, text, MaxFileBytes, &read, nullptr)) problem = "the settings file can't be read";
    else
    {
        text[read] = 0;
        _locale_t c = _create_locale(LC_NUMERIC, "C");
        for (char* line = text; line && *line;)
        {
            char* next = strchr(line, '\n');
            if (next) *next++ = 0;
            ParseLine(line, chain, rate, c);
            line = next;
        }
        _free_locale(c);
        result = Load::Ok;
    }
    delete[] text;
    CloseHandle(h);
    return result;
}

// What the endpoint runs, for KOS Equalizer to read back: one line in %TEMP%\KosEqApo-<guid>.txt (audiodg's TEMP).
void WriteStatus(const wchar_t* guid, const char* text)
{
    wchar_t temp[MAX_PATH], path[MAX_PATH];
    DWORD n = GetTempPathW(MAX_PATH, temp);
    if (n == 0 || n >= MAX_PATH || swprintf_s(path, L"%sKosEqApo-%s.txt", temp, guid) < 0) return;
    HANDLE h = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    DWORD written = 0;
    WriteFile(h, text, static_cast<DWORD>(strlen(text)), &written, nullptr);
    CloseHandle(h);
}

// Shared by an APO and its watcher thread, counted: a watcher that outlives the APO's wait for it (stuck in I/O) still has it.
struct Shared
{
    std::atomic<long> refs{1};
    HANDLE stop = nullptr;
    HMODULE module = nullptr;          // the watcher's reference to this DLL, dropped as it exits
    wchar_t guid[64] = {};
    double rate = 48000;
    std::atomic<Chain*> chain{nullptr};
    std::atomic<int> busy{0};          // the audio thread is inside APOProcess
    std::atomic<unsigned> processed{0}; // buffers the audio thread finished
    Chain* retired = nullptr;          // watcher only: the chain swapped out last, freed once no buffer can be using it
    unsigned retiredAt = 0;
    unsigned generation = 0;
    FILETIME stamp = {};

    void AddRef() { refs++; }
    void Release() { if (--refs == 0) delete this; }
    ~Shared()
    {
        delete chain.load();
        delete retired;
        if (stop) CloseHandle(stop);
    }
};

// Rereads the settings when the file changed (or always, with force): missing → flat, refused → the previous chain stays.
void Reload(Shared& s, bool force)
{
    if (!s.guid[0]) return;
    if (s.retired)
    {
        // A buffer that took the old chain started before the swap: with the audio thread outside APOProcess, or two buffers
        // finished since, none can still hold it. Otherwise wait for the next round.
        if (s.busy.load() != 0 && s.processed.load() - s.retiredAt < 2) return;
        delete s.retired;
        s.retired = nullptr;
    }
    wchar_t data[MAX_PATH] = L"C:\\ProgramData", path[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(L"ProgramData", data, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) wcscpy_s(data, L"C:\\ProgramData");
    swprintf_s(path, L"%s\\TheKOS\\Audio\\Eq\\%s.eq", data, s.guid);
    WIN32_FILE_ATTRIBUTE_DATA info = {};
    FILETIME stamp = {};
    if (GetFileAttributesExW(path, GetFileExInfoStandard, &info)) stamp = info.ftLastWriteTime;
    if (!force && CompareFileTime(&stamp, &s.stamp) == 0) return;
    s.stamp = stamp;
    auto next = new (std::nothrow) Chain();
    if (!next) return;
    const char* problem = "";
    char status[160];
    Load load = ReadSettings(s.guid, *next, s.rate, problem);
    double peak = load == Load::Ok ? PeakDb(*next, s.rate) : 0;
    if (load == Load::Refused || peak > MaxBoost + 0.5)
    {
        if (load != Load::Refused) problem = "the curve boosts more than +24 dB";
        sprintf_s(status, "refused: %s\r\n", problem);
        WriteStatus(s.guid, status);
        OutputDebugStringA("KosEqApo: settings refused: ");
        OutputDebugStringA(problem);
        OutputDebugStringA("\n");
        delete next;
        return;
    }
    if (load == Load::Missing) { next->count = 0; next->preamp = 1; next->preampDb = 0; }
    next->generation = ++s.generation;
    Chain* previous = s.chain.exchange(next);
    s.retired = previous;
    s.retiredAt = s.processed.load();
    if (load == Load::Missing) sprintf_s(status, "flat: %s\r\n", problem);
    else sprintf_s(status, "applied: preamp %.2f dB, %d bands, %.0f Hz\r\n", next->preampDb, next->count, s.rate);
    WriteStatus(s.guid, status);
}

// Watcher thread: rereads the settings when the file changes (checked twice a second).
DWORD WINAPI Watch(void* param)
{
    auto s = static_cast<Shared*>(param);
    while (WaitForSingleObject(s->stop, 500) == WAIT_TIMEOUT) Reload(*s, false);
    HMODULE module = s->module;
    s->Release();
    FreeLibraryAndExitThread(module, 0);
}
}

// Whether the audio engine may connect this format: 32-bit float, any channel count and rate within the engine's limits, the
// same on input and output. No other format is suggested (the engine hands system effects float; a suggestion would need
// audiomediatypecrt.lib, which needs ATL).
static HRESULT Supported(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** supported)
{
    if (!requested || !supported) return E_POINTER;
    *supported = nullptr;
    UNCOMPRESSEDAUDIOFORMAT f = {};
    if (FAILED(requested->GetUncompressedAudioFormat(&f))) return APOERR_FORMAT_NOT_SUPPORTED;
    if (f.dwSamplesPerFrame < AUDIO_MIN_CHANNELS || f.dwSamplesPerFrame > AUDIO_MAX_CHANNELS || f.fFramesPerSecond < AUDIO_MIN_FRAMERATE || f.fFramesPerSecond > AUDIO_MAX_FRAMERATE)
        return APOERR_FORMAT_NOT_SUPPORTED;
    UNCOMPRESSEDAUDIOFORMAT other = {};
    if (opposite && SUCCEEDED(opposite->GetUncompressedAudioFormat(&other)) && (other.dwSamplesPerFrame != f.dwSamplesPerFrame || other.fFramesPerSecond != f.fFramesPerSecond))
        return APOERR_FORMAT_NOT_SUPPORTED;
    if (f.guidFormatType == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT && f.dwBytesPerSampleContainer == 4 && f.dwValidBitsPerSample == 32)
    {
        requested->AddRef();
        *supported = requested;
        return S_OK;
    }
    return APOERR_FORMAT_NOT_SUPPORTED;
}

static bool IsFloat(IAudioMediaType* type, UNCOMPRESSEDAUDIOFORMAT& f)
{
    return type && SUCCEEDED(type->GetUncompressedAudioFormat(&f)) && f.guidFormatType == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
        && f.dwBytesPerSampleContainer == 4 && f.dwValidBitsPerSample == 32 && f.dwSamplesPerFrame >= 1 && f.dwSamplesPerFrame <= AUDIO_MAX_CHANNELS;
}

class CKosEqApo final : public IAudioProcessingObject, public IAudioProcessingObjectRT, public IAudioProcessingObjectConfiguration, public IAudioSystemEffects
{
public:
    static APO_REG_PROPERTIES Registration;

    CKosEqApo() { g_objects++; }
    ~CKosEqApo()
    {
        StopWatcher();
        Free();
        g_objects--;
    }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID iid, void** out) override
    {
        if (!out) return E_POINTER;
        if (iid == __uuidof(IUnknown) || iid == __uuidof(IAudioProcessingObject)) *out = static_cast<IAudioProcessingObject*>(this);
        else if (iid == __uuidof(IAudioProcessingObjectRT)) *out = static_cast<IAudioProcessingObjectRT*>(this);
        else if (iid == __uuidof(IAudioProcessingObjectConfiguration)) *out = static_cast<IAudioProcessingObjectConfiguration*>(this);
        else if (iid == __uuidof(IAudioSystemEffects)) *out = static_cast<IAudioSystemEffects*>(this);
        else { *out = nullptr; return E_NOINTERFACE; }
        AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++m_refs; }
    STDMETHODIMP_(ULONG) Release() override
    {
        ULONG left = --m_refs;
        if (left == 0) delete this;
        return left;
    }

    // Which endpoint this instance serves: the last device of the collection system effects get is always the endpoint (the
    // others are the topology devices the driver's connectors belong to).
    STDMETHODIMP Initialize(UINT32 size, BYTE* data) override
    {
        if (!data || size < sizeof(APOInitBaseStruct)) return E_INVALIDARG;
        auto base = reinterpret_cast<APOInitBaseStruct*>(data);
        if (base->clsid != CLSID_KosEqApo) return E_INVALIDARG;
        m_guid[0] = 0;
        if (size >= sizeof(APOInitSystemEffects2) && base->cbSize >= sizeof(APOInitSystemEffects2))
            EndpointGuid(reinterpret_cast<APOInitSystemEffects2*>(data)->pDeviceCollection);
        else if (size >= sizeof(APOInitSystemEffects) && base->cbSize >= sizeof(APOInitSystemEffects))
            EndpointGuid(reinterpret_cast<APOInitSystemEffects*>(data)->pDeviceCollection);
        m_initialized = true;
        return S_OK;
    }

    STDMETHODIMP Reset() override { return S_OK; }
    STDMETHODIMP GetLatency(HNSTIME* time) override { if (!time) return E_POINTER; *time = 0; return S_OK; }
    STDMETHODIMP GetRegistrationProperties(APO_REG_PROPERTIES** props) override
    {
        if (!props) return E_POINTER;
        *props = static_cast<APO_REG_PROPERTIES*>(CoTaskMemAlloc(sizeof(APO_REG_PROPERTIES)));
        if (!*props) return E_OUTOFMEMORY;
        **props = Registration;
        return S_OK;
    }
    STDMETHODIMP IsInputFormatSupported(IAudioMediaType* output, IAudioMediaType* requested, IAudioMediaType** supported) override { return Supported(output, requested, supported); }
    STDMETHODIMP IsOutputFormatSupported(IAudioMediaType* input, IAudioMediaType* requested, IAudioMediaType** supported) override { return Supported(input, requested, supported); }
    STDMETHODIMP GetInputChannelCount(UINT32* count) override { if (!count) return E_POINTER; *count = m_channels; return S_OK; }
    STDMETHODIMP_(UINT32) CalcInputFrames(UINT32 frames) override { return frames; }
    STDMETHODIMP_(UINT32) CalcOutputFrames(UINT32 frames) override { return frames; }

    STDMETHODIMP LockForProcess(UINT32 inCount, APO_CONNECTION_DESCRIPTOR** in, UINT32 outCount, APO_CONNECTION_DESCRIPTOR** out) override
    {
        if (!m_initialized) return APOERR_NOT_INITIALIZED;
        if (m_locked) return APOERR_APO_LOCKED;
        if (inCount != 1 || outCount != 1 || !in || !out || !in[0] || !out[0]) return APOERR_NUM_CONNECTIONS_INVALID;
        UNCOMPRESSEDAUDIOFORMAT fi = {}, fo = {};
        if (!IsFloat(in[0]->pFormat, fi) || !IsFloat(out[0]->pFormat, fo) || fi.dwSamplesPerFrame != fo.dwSamplesPerFrame || fi.fFramesPerSecond != fo.fFramesPerSecond)
            return APOERR_INVALID_CONNECTION_FORMAT;
        if (out[0]->u32MaxFrameCount < in[0]->u32MaxFrameCount) return APOERR_INVALID_OUTPUT_MAXFRAMECOUNT;
        m_channels = fi.dwSamplesPerFrame;
        size_t bytes = sizeof(double) * 2 * MaxBands * m_channels;
        HRESULT hr = AERT_Allocate(bytes, reinterpret_cast<void**>(&m_state));
        if (FAILED(hr)) return hr;
        ZeroMemory(m_state, bytes);
        m_stateBytes = bytes;
        m_ranGeneration = ~0u;
        auto shared = new (std::nothrow) Shared();
        if (!shared) { Free(); return E_OUTOFMEMORY; }
        shared->rate = fi.fFramesPerSecond;
        wcscpy_s(shared->guid, m_guid);
        Reload(*shared, true);
        m_shared = shared;
        StartWatcher();
        m_locked = true;
        return S_OK;
    }

    STDMETHODIMP UnlockForProcess() override
    {
        if (!m_locked) return APOERR_ALREADY_UNLOCKED;
        StopWatcher();
        Free();
        m_locked = false;
        return S_OK;
    }

    // The audio thread: no locks, no allocation, no I/O. Float samples, interleaved, in place or copied.
    STDMETHODIMP_(void) APOProcess(UINT32 inCount, APO_CONNECTION_PROPERTY** in, UINT32 outCount, APO_CONNECTION_PROPERTY** out) override
    {
        if (inCount < 1 || outCount < 1) return;
        auto input = in[0];
        auto output = out[0];
        UINT32 frames = input->u32ValidFrameCount;
        auto src = reinterpret_cast<const float*>(input->pBuffer);
        auto dst = reinterpret_cast<float*>(output->pBuffer);
        Shared* s = m_shared;
        if (input->u32BufferFlags == BUFFER_SILENT || !s)
        {
            if (dst != src)
            {
                if (input->u32BufferFlags == BUFFER_SILENT) ZeroMemory(dst, sizeof(float) * frames * m_channels);
                else CopyMemory(dst, src, sizeof(float) * frames * m_channels);
            }
            output->u32ValidFrameCount = frames;
            output->u32BufferFlags = input->u32BufferFlags == BUFFER_SILENT ? BUFFER_SILENT : BUFFER_VALID;
            return;
        }
        s->busy.store(1); // before taking the chain (sequentially consistent): the watcher frees a retired chain only when this is 0
        Chain* chain = s->chain.load();
        if (!chain || (chain->count == 0 && chain->preamp == 1.0))
        {
            if (dst != src) CopyMemory(dst, src, sizeof(float) * frames * m_channels);
        }
        else
        {
            if (chain->generation != m_ranGeneration)
            {
                ZeroMemory(m_state, m_stateBytes); // other filters: their history doesn't fit
                m_ranGeneration = chain->generation;
            }
            const UINT32 channels = m_channels;
            bool blewUp = false;
            for (UINT32 ch = 0; ch < channels; ch++)
            {
                double* z = m_state + ch * 2 * MaxBands;
                for (UINT32 i = 0; i < frames; i++)
                {
                    double x = src[i * channels + ch] * chain->preamp;
                    for (int b = 0; b < chain->count; b++)
                    {
                        const Coefficients& k = chain->filters[b];
                        double y = k.b0 * x + z[2 * b];               // transposed direct form II
                        z[2 * b] = k.b1 * x - k.a1 * y + z[2 * b + 1];
                        z[2 * b + 1] = k.b2 * x - k.a2 * y;
                        x = y;
                    }
                    if (!(x > -1e6 && x < 1e6)) blewUp = true; // NaN fails both
                    dst[i * channels + ch] = static_cast<float>(x);
                }
            }
            if (blewUp)
            {
                // A NaN/Inf in (or a filter gone unstable) would stay in the history for good: silence this buffer, start over.
                ZeroMemory(m_state, m_stateBytes);
                ZeroMemory(dst, sizeof(float) * frames * channels);
            }
        }
        output->u32ValidFrameCount = frames;
        output->u32BufferFlags = BUFFER_VALID;
        s->processed.fetch_add(1);
        s->busy.store(0);
    }

private:
    std::atomic<ULONG> m_refs{1};
    bool m_initialized = false, m_locked = false;
    wchar_t m_guid[64] = {};
    UINT32 m_channels = 0;
    double* m_state = nullptr;
    size_t m_stateBytes = 0;
    unsigned m_ranGeneration = ~0u;
    Shared* m_shared = nullptr;
    HANDLE m_watcher = nullptr;

    // The endpoint's GUID (what follows the id's last '.', hex digits and '-' only, lower case) names its settings file.
    void EndpointGuid(IMMDeviceCollection* devices)
    {
        IMMDevice* device = nullptr;
        UINT count = 0;
        if (!devices || FAILED(devices->GetCount(&count)) || count == 0 || FAILED(devices->Item(count - 1, &device)) || !device) return;
        LPWSTR id = nullptr;
        if (SUCCEEDED(device->GetId(&id)) && id)
        {
            const wchar_t* guid = wcsrchr(id, L'.');
            guid = guid ? guid + 1 : id;
            size_t j = 0;
            for (size_t i = 0; guid[i] && j < 60; i++)
                if (iswxdigit(guid[i]) || guid[i] == L'-') m_guid[j++] = towlower(guid[i]);
            m_guid[j] = 0;
            CoTaskMemFree(id);
        }
        device->Release();
    }

    void StartWatcher()
    {
        Shared* s = m_shared;
        s->stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!s->stop || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(&Watch), &s->module)) return;
        s->AddRef();
        m_watcher = CreateThread(nullptr, 0, Watch, s, 0, nullptr);
        if (!m_watcher) { s->Release(); FreeLibrary(s->module); }
    }

    // Asks the watcher to stop and waits a moment; one that doesn't stop in time keeps the shared state and the DLL alive itself.
    void StopWatcher()
    {
        if (!m_shared) return;
        if (m_shared->stop) SetEvent(m_shared->stop);
        if (m_watcher)
        {
            WaitForSingleObject(m_watcher, 2000);
            CloseHandle(m_watcher);
            m_watcher = nullptr;
        }
        m_shared->Release();
        m_shared = nullptr;
    }

    void Free()
    {
        if (m_state) { AERT_Free(m_state); m_state = nullptr; }
    }
};

APO_REG_PROPERTIES CKosEqApo::Registration = {
    CLSID_KosEqApo,
    static_cast<APO_FLAG>(APO_FLAG_SAMPLESPERFRAME_MUST_MATCH | APO_FLAG_FRAMESPERSECOND_MUST_MATCH | APO_FLAG_BITSPERSAMPLE_MUST_MATCH | APO_FLAG_INPLACE),
    L"KOS Equalizer", L"KOS", 1, 0, 1, 1, 1, 1, ULONG_MAX, 1, { __uuidof(IAudioProcessingObject) } };

class ClassFactory final : public IClassFactory
{
public:
    STDMETHODIMP QueryInterface(REFIID iid, void** out) override
    {
        if (!out) return E_POINTER;
        if (iid == __uuidof(IUnknown) || iid == __uuidof(IClassFactory)) { *out = this; AddRef(); return S_OK; }
        *out = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return 2; }  // static instance
    STDMETHODIMP_(ULONG) Release() override { return 1; }
    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID iid, void** out) override
    {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        auto apo = new (std::nothrow) CKosEqApo();
        if (!apo) return E_OUTOFMEMORY;
        HRESULT hr = apo->QueryInterface(iid, out);
        apo->Release();
        return hr;
    }
    STDMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) g_locks++; else g_locks--;
        return S_OK;
    }
};

static ClassFactory g_factory;

STDAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    if (clsid != CLSID_KosEqApo) return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(iid, out);
}

STDAPI DllCanUnloadNow() { return g_objects == 0 && g_locks == 0 ? S_OK : S_FALSE; }

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, void*)
{
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(module);
    return TRUE;
}
