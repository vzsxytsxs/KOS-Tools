// KOS Equalizer: an audio processing object (APO) the audio engine (audiodg.exe) loads for one endpoint. It runs a chain of
// biquad filters and a preamp over the endpoint's float samples. Its settings are a small text file per endpoint, written by
// KOS Toolbox and KOS Engine; a watcher thread reads it and swaps in new coefficients, so the audio thread never does I/O,
// never takes a lock and never allocates.
//
// It implements the three APO interfaces itself (the SDK's base class needs ATL). Float samples only, the same format on both
// sides, processed in place.
//
// Spike status: built and driven by a host harness only. Nothing registers it; where audiodg's account can read the settings
// and what Initialize receives on a real endpoint are open (see ConfigPath and EndpointId).

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
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
static HMODULE g_module;

namespace
{
constexpr int MaxBands = 64;
constexpr double Pi = 3.14159265358979323846;

enum class FilterType { Peak, LowShelf, HighShelf, LowPass, HighPass, Notch, BandPass };

struct Band { FilterType type; double frequency, gain, q; };

struct Coefficients { double b0, b1, b2, a1, a2; };

// What the audio thread runs: built on the watcher thread, swapped in whole.
struct Chain
{
    double preamp = 1.0; // linear
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

bool ParseType(const char* word, FilterType& type)
{
    static const struct { const char* name; FilterType type; } names[] = {
        { "peak", FilterType::Peak }, { "lowshelf", FilterType::LowShelf }, { "highshelf", FilterType::HighShelf },
        { "lowpass", FilterType::LowPass }, { "highpass", FilterType::HighPass }, { "notch", FilterType::Notch }, { "bandpass", FilterType::BandPass } };
    for (auto& n : names)
        if (_stricmp(word, n.name) == 0) { type = n.type; return true; }
    return false;
}

// The settings file, one line each: "preamp -3.5" and "peak 1000 6 1.41" (type, frequency Hz, gain dB, Q). Anything else is
// skipped; numbers use a decimal point. Returns false when the file can't be read (the chain is then flat).
bool ReadSettings(const wchar_t* path, Chain& chain, double rate)
{
    chain.preamp = 1.0;
    chain.count = 0;
    FILE* file = nullptr;
    if (_wfopen_s(&file, path, L"rb") != 0 || !file) return false;
    char line[256];
    _locale_t c = _create_locale(LC_NUMERIC, "C");
    while (fgets(line, sizeof line, file))
    {
        char word[32] = {};
        double f = 0, g = 0, q = 0;
        if (sscanf_s(line, "%31s", word, (unsigned)sizeof word) != 1 || word[0] == '#') continue;
        char* after = strstr(line, word) + strlen(word); // past any leading spaces and the word
        if (_stricmp(word, "preamp") == 0)
        {
            char* end = nullptr;
            const char* number = after;
            double db = _strtod_l(number, &end, c);
            if (end != number && db >= -30 && db <= 30) chain.preamp = std::pow(10.0, db / 20);
            continue;
        }
        FilterType type;
        if (!ParseType(word, type) || chain.count >= MaxBands) continue;
        char* p = after;
        char* end = nullptr;
        f = _strtod_l(p, &end, c); if (end == p) continue; p = end;
        g = _strtod_l(p, &end, c); if (end == p) continue; p = end;
        q = _strtod_l(p, &end, c); if (end == p) continue;
        if (!(f >= 10 && f <= 24000) || !(g >= -30 && g <= 30) || !(q >= 0.1 && q <= 30)) continue;
        chain.filters[chain.count++] = Design({ type, f, g, q }, rate);
    }
    _free_locale(c);
    fclose(file);
    return true;
}
}

// Where an endpoint's settings live. Open question for the spike: audiodg runs as LOCAL SERVICE; this folder must be readable
// by it. Kept in one place so the answer changes one line.
static void ConfigPath(const wchar_t* endpointId, wchar_t* path, size_t size)
{
    wchar_t data[MAX_PATH] = L"C:\\ProgramData";
    GetEnvironmentVariableW(L"ProgramData", data, MAX_PATH);
    // The id is "{0.0.0.00000000}.{guid}": the GUID after the last dot names the file.
    const wchar_t* guid = wcsrchr(endpointId, L'.');
    guid = guid ? guid + 1 : endpointId;
    wchar_t clean[64] = {};
    for (size_t i = 0, j = 0; guid[i] && j < 60; i++)
        if (iswxdigit(guid[i]) || guid[i] == L'-') clean[j++] = towlower(guid[i]);
    swprintf_s(path, size, L"%s\\TheKOS\\Audio\\Eq\\%s.eq", data, clean);
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

    // Which endpoint this instance serves: system effects get the device collection and the index of their device.
    STDMETHODIMP Initialize(UINT32 size, BYTE* data) override
    {
        if (!data || size < sizeof(APOInitBaseStruct)) return E_INVALIDARG;
        auto base = reinterpret_cast<APOInitBaseStruct*>(data);
        if (base->clsid != CLSID_KosEqApo) return E_INVALIDARG;
        m_endpoint[0] = 0;
        if (size >= sizeof(APOInitSystemEffects2) && base->cbSize >= sizeof(APOInitSystemEffects2))
            EndpointId(reinterpret_cast<APOInitSystemEffects2*>(data)->pDeviceCollection, reinterpret_cast<APOInitSystemEffects2*>(data)->nSoftwareIoDeviceInCollection);
        else if (size >= sizeof(APOInitSystemEffects) && base->cbSize >= sizeof(APOInitSystemEffects))
            EndpointId(reinterpret_cast<APOInitSystemEffects*>(data)->pDeviceCollection, 0);
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
        m_rate = fi.fFramesPerSecond;
        size_t bytes = sizeof(double) * 2 * MaxBands * m_channels;
        HRESULT hr = AERT_Allocate(bytes, reinterpret_cast<void**>(&m_state));
        if (FAILED(hr)) return hr;
        ZeroMemory(m_state, bytes);
        m_stateBytes = bytes;
        m_ranGeneration = ~0u;
        Reload(true);
        m_stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        m_watcher = m_stop ? CreateThread(nullptr, 0, Watch, this, 0, nullptr) : nullptr;
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
        if (input->u32BufferFlags == BUFFER_SILENT)
        {
            if (dst != src) ZeroMemory(dst, sizeof(float) * frames * m_channels);
            output->u32ValidFrameCount = frames;
            output->u32BufferFlags = BUFFER_SILENT;
            return;
        }
        Chain* chain = m_chain.load(std::memory_order_acquire);
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
                    dst[i * channels + ch] = static_cast<float>(x);
                }
            }
        }
        output->u32ValidFrameCount = frames;
        output->u32BufferFlags = BUFFER_VALID;
    }

private:
    std::atomic<ULONG> m_refs{1};
    bool m_initialized = false, m_locked = false;
    wchar_t m_endpoint[256] = {};
    UINT32 m_channels = 0;
    float m_rate = 48000;
    double* m_state = nullptr;
    size_t m_stateBytes = 0;
    unsigned m_ranGeneration = ~0u;
    std::atomic<Chain*> m_chain{nullptr};
    Chain* m_retired = nullptr; // the chain before the current one: the audio thread may still be finishing a buffer with it
    unsigned m_generation = 0;
    FILETIME m_stamp = {};
    HANDLE m_stop = nullptr, m_watcher = nullptr;

    void EndpointId(IMMDeviceCollection* devices, UINT index)
    {
        IMMDevice* device = nullptr;
        if (!devices || FAILED(devices->Item(index, &device)) || !device) return;
        LPWSTR id = nullptr;
        if (SUCCEEDED(device->GetId(&id)) && id)
        {
            wcsncpy_s(m_endpoint, id, _TRUNCATE);
            CoTaskMemFree(id);
        }
        device->Release();
    }

    // Watcher thread: rereads the settings when the file changes (checked twice a second).
    static DWORD WINAPI Watch(void* self)
    {
        auto apo = static_cast<CKosEqApo*>(self);
        while (WaitForSingleObject(apo->m_stop, 500) == WAIT_TIMEOUT) apo->Reload(false);
        return 0;
    }

    void Reload(bool force)
    {
        if (!m_endpoint[0]) return;
        wchar_t path[MAX_PATH];
        ConfigPath(m_endpoint, path, MAX_PATH);
        WIN32_FILE_ATTRIBUTE_DATA info = {};
        FILETIME stamp = {};
        if (GetFileAttributesExW(path, GetFileExInfoStandard, &info)) stamp = info.ftLastWriteTime;
        if (!force && CompareFileTime(&stamp, &m_stamp) == 0) return;
        m_stamp = stamp;
        auto next = new (std::nothrow) Chain();
        if (!next) return;
        ReadSettings(path, *next, m_rate);
        next->generation = ++m_generation;
        Chain* previous = m_chain.exchange(next, std::memory_order_acq_rel);
        // A buffer takes ~10 ms and swaps are at least 500 ms apart: the chain retired the time before is no longer in use.
        delete m_retired;
        m_retired = previous;
    }

    void StopWatcher()
    {
        if (m_stop) SetEvent(m_stop);
        if (m_watcher) { WaitForSingleObject(m_watcher, INFINITE); CloseHandle(m_watcher); m_watcher = nullptr; }
        if (m_stop) { CloseHandle(m_stop); m_stop = nullptr; }
    }

    void Free()
    {
        delete m_chain.exchange(nullptr);
        delete m_retired;
        m_retired = nullptr;
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
    if (reason == DLL_PROCESS_ATTACH) { g_module = module; DisableThreadLibraryCalls(module); }
    return TRUE;
}
