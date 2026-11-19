// Test host: drives KosEqApo.dll the way the audio engine does (class factory, Initialize with a device collection naming the
// endpoint, LockForProcess with float stereo at 48 kHz, APOProcess on buffers) and checks what comes out. Nothing is
// registered: the DLL is loaded from its folder. The settings file for the fake endpoint is created and deleted again.
// Run it elevated: files the DLL accepts must be owned by Administrators (an elevated process's default owner).

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <aclapi.h>
#include <initguid.h>
#include <mmdeviceapi.h>
#include <audioenginebaseapo.h>
#include <audiomediatype.h>
#include <ks.h>
#include <ksmedia.h>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

DEFINE_GUID(CLSID_KosEqApo, 0x610c4861, 0xeb61, 0x417d, 0xa6, 0x53, 0x45, 0xb0, 0x3d, 0x49, 0x31, 0x23);
static const wchar_t* EndpointId = L"{0.0.0.00000000}.{6b05e7a1-0000-4c6f-9a11-000000000042}";
static int failures;

static void Check(bool ok, const char* what)
{
    printf("%s %s\n", ok ? "PASS" : "FAIL", what);
    if (!ok) failures++;
}

// Just enough of a device and a collection for Initialize to read the endpoint id.
struct FakeDevice : IMMDevice
{
    const wchar_t* deviceId;
    explicit FakeDevice(const wchar_t* id) : deviceId(id) {}
    STDMETHODIMP QueryInterface(REFIID, void** out) override { *out = nullptr; return E_NOINTERFACE; }
    STDMETHODIMP_(ULONG) AddRef() override { return 2; }
    STDMETHODIMP_(ULONG) Release() override { return 1; }
    STDMETHODIMP Activate(REFIID, DWORD, PROPVARIANT*, void**) override { return E_NOTIMPL; }
    STDMETHODIMP OpenPropertyStore(DWORD, IPropertyStore**) override { return E_NOTIMPL; }
    STDMETHODIMP GetId(LPWSTR* id) override
    {
        size_t bytes = (wcslen(deviceId) + 1) * sizeof(wchar_t);
        *id = static_cast<LPWSTR>(CoTaskMemAlloc(bytes));
        memcpy(*id, deviceId, bytes);
        return S_OK;
    }
    STDMETHODIMP GetState(DWORD* state) override { *state = DEVICE_STATE_ACTIVE; return S_OK; }
};

// As the audio engine passes it: the driver's topology device first, the endpoint always last.
struct FakeCollection : IMMDeviceCollection
{
    FakeDevice topology{ L"{2}.topology-of-the-driver" }, endpoint{ EndpointId };
    STDMETHODIMP QueryInterface(REFIID, void** out) override { *out = nullptr; return E_NOINTERFACE; }
    STDMETHODIMP_(ULONG) AddRef() override { return 2; }
    STDMETHODIMP_(ULONG) Release() override { return 1; }
    STDMETHODIMP GetCount(UINT* count) override { *count = 2; return S_OK; }
    STDMETHODIMP Item(UINT index, IMMDevice** out) override { if (index > 1) return E_INVALIDARG; *out = index == 0 ? &topology : &endpoint; return S_OK; }
};

// A media type holding one uncompressed format (the engine passes its own; the SDK's helper library needs ATL).
struct MediaType : IAudioMediaType
{
    UNCOMPRESSEDAUDIOFORMAT format;
    explicit MediaType(const UNCOMPRESSEDAUDIOFORMAT& f) : format(f) {}
    STDMETHODIMP QueryInterface(REFIID iid, void** out) override
    {
        if (iid == __uuidof(IUnknown) || iid == __uuidof(IAudioMediaType)) { *out = this; return S_OK; }
        *out = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return 2; }
    STDMETHODIMP_(ULONG) Release() override { return 1; }
    STDMETHODIMP IsCompressedFormat(BOOL* compressed) override { *compressed = FALSE; return S_OK; }
    STDMETHODIMP IsEqual(IAudioMediaType*, DWORD*) override { return E_NOTIMPL; }
    STDMETHODIMP_(const WAVEFORMATEX*) GetAudioFormat() override { return nullptr; }
    STDMETHODIMP GetUncompressedAudioFormat(UNCOMPRESSEDAUDIOFORMAT* f) override { *f = format; return S_OK; }
};

static std::wstring SettingsPath()
{
    wchar_t data[MAX_PATH];
    GetEnvironmentVariableW(L"ProgramData", data, MAX_PATH);
    return std::wstring(data) + L"\\TheKOS\\Audio\\Eq\\6b05e7a1-0000-4c6f-9a11-000000000042.eq";
}

static void WriteSettings(const char* text, const wchar_t* path = nullptr)
{
    FILE* f = nullptr;
    _wfopen_s(&f, path ? path : SettingsPath().c_str(), L"wb");
    if (f) { fputs(text, f); fclose(f); }
}

// The DLL's status line for the fake endpoint (in this process's TEMP, as audiodg's is in its own).
static std::string Status()
{
    wchar_t temp[MAX_PATH];
    GetTempPathW(MAX_PATH, temp);
    FILE* f = nullptr;
    std::string text;
    if (_wfopen_s(&f, (std::wstring(temp) + L"KosEqApo-6b05e7a1-0000-4c6f-9a11-000000000042.txt").c_str(), L"rb") == 0 && f)
    {
        char line[200] = {};
        if (fgets(line, sizeof line, f)) text = line;
        fclose(f);
    }
    return text;
}

// Replaces the settings with a file this user owns (not Administrators): what a standard user could plant if a folder let them.
static bool PlantUserOwned(const char* text)
{
    std::wstring tmp = SettingsPath() + L".tmp";
    WriteSettings(text, tmp.c_str());
    HANDLE token = nullptr;
    BYTE buffer[256];
    DWORD size = 0;
    bool ok = OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token) && GetTokenInformation(token, TokenUser, buffer, sizeof buffer, &size);
    if (token) CloseHandle(token);
    ok = ok && SetNamedSecurityInfoW(const_cast<wchar_t*>(tmp.c_str()), SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION,
        reinterpret_cast<TOKEN_USER*>(buffer)->User.Sid, nullptr, nullptr, nullptr) == ERROR_SUCCESS;
    return ok && MoveFileExW(tmp.c_str(), SettingsPath().c_str(), MOVEFILE_REPLACE_EXISTING);
}

int main()
{
    const UINT32 Frames = 480, Channels = 2;
    const float Rate = 48000;
    wchar_t self[MAX_PATH];
    GetModuleFileNameW(nullptr, self, MAX_PATH);
    *wcsrchr(self, L'\\') = 0;
    std::wstring dllPath = std::wstring(self) + L"\\KosEqApo.dll";
    HMODULE dll = LoadLibraryW(dllPath.c_str());
    Check(dll != nullptr, "the DLL loads");
    if (!dll) return 1;
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    // The settings folder: created for the test only if it isn't there, removed again afterwards.
    wchar_t data[MAX_PATH];
    GetEnvironmentVariableW(L"ProgramData", data, MAX_PATH);
    std::wstring audio = std::wstring(data) + L"\\TheKOS\\Audio", eq = audio + L"\\Eq";
    bool madeAudio = CreateDirectoryW(audio.c_str(), nullptr) != 0, madeEq = CreateDirectoryW(eq.c_str(), nullptr) != 0;
    WriteSettings("# test\npeak 1000 6 1\n");

    auto getClass = reinterpret_cast<HRESULT(STDAPICALLTYPE*)(REFCLSID, REFIID, void**)>(GetProcAddress(dll, "DllGetClassObject"));
    IClassFactory* factory = nullptr;
    Check(getClass && SUCCEEDED(getClass(CLSID_KosEqApo, __uuidof(IClassFactory), reinterpret_cast<void**>(&factory))), "class factory");
    IAudioProcessingObject* apo = nullptr;
    Check(factory && SUCCEEDED(factory->CreateInstance(nullptr, __uuidof(IAudioProcessingObject), reinterpret_cast<void**>(&apo))), "an instance");
    if (!apo) return 1;
    IAudioProcessingObjectRT* rt = nullptr;
    IAudioProcessingObjectConfiguration* config = nullptr;
    IAudioSystemEffects* sfx = nullptr;
    apo->QueryInterface(__uuidof(IAudioProcessingObjectRT), reinterpret_cast<void**>(&rt));
    apo->QueryInterface(__uuidof(IAudioProcessingObjectConfiguration), reinterpret_cast<void**>(&config));
    Check(rt && config && SUCCEEDED(apo->QueryInterface(__uuidof(IAudioSystemEffects), reinterpret_cast<void**>(&sfx))), "real-time, configuration and system-effects interfaces");
    if (sfx) sfx->Release();

    APO_REG_PROPERTIES* reg = nullptr;
    Check(SUCCEEDED(apo->GetRegistrationProperties(&reg)) && reg && reg->clsid == CLSID_KosEqApo && wcscmp(reg->szFriendlyName, L"KOS Equalizer") == 0, "registration properties");
    if (reg) CoTaskMemFree(reg);

    FakeCollection devices;
    APOInitSystemEffects2 init = {};
    init.APOInit.cbSize = sizeof init;
    init.APOInit.clsid = CLSID_KosEqApo;
    init.pDeviceCollection = &devices;
    init.nSoftwareIoDeviceInCollection = 0; // the topology device: not the one that names the settings
    Check(SUCCEEDED(apo->Initialize(sizeof init, reinterpret_cast<BYTE*>(&init))), "Initialize with the endpoint's device collection");

    UNCOMPRESSEDAUDIOFORMAT format = { KSDATAFORMAT_SUBTYPE_IEEE_FLOAT, Channels, 4, 32, Rate, KSAUDIO_SPEAKER_STEREO };
    MediaType media(format);
    IAudioMediaType* type = &media;
    UNCOMPRESSEDAUDIOFORMAT pcm = format;
    pcm.guidFormatType = KSDATAFORMAT_SUBTYPE_PCM;
    pcm.dwBytesPerSampleContainer = 2;
    pcm.dwValidBitsPerSample = 16;
    MediaType wrong(pcm);
    IAudioMediaType* answer = nullptr;
    Check(apo->IsInputFormatSupported(nullptr, type, &answer) == S_OK && answer == type, "float input accepted");
    Check(apo->IsInputFormatSupported(nullptr, &wrong, &answer) == APOERR_FORMAT_NOT_SUPPORTED, "16-bit PCM refused");
    std::vector<float> buffer(Frames * Channels);
    APO_CONNECTION_DESCRIPTOR in = { APO_CONNECTION_BUFFER_TYPE_EXTERNAL, reinterpret_cast<UINT_PTR>(buffer.data()), Frames, type, APO_CONNECTION_DESCRIPTOR_SIGNATURE };
    APO_CONNECTION_DESCRIPTOR out = in;
    APO_CONNECTION_DESCRIPTOR* ins[] = { &in };
    APO_CONNECTION_DESCRIPTOR* outs[] = { &out };
    HRESULT hr = config->LockForProcess(1, ins, 1, outs);
    printf("     LockForProcess: 0x%08lX\n", hr);
    Check(SUCCEEDED(hr), "LockForProcess (float, stereo, 48 kHz, in place)");
    if (FAILED(hr)) return 1;

    APO_CONNECTION_PROPERTY pin = { reinterpret_cast<UINT_PTR>(buffer.data()), Frames, BUFFER_VALID, APO_CONNECTION_PROPERTY_SIGNATURE };
    APO_CONNECTION_PROPERTY pout = pin;
    APO_CONNECTION_PROPERTY* pins[] = { &pin };
    APO_CONNECTION_PROPERTY* pouts[] = { &pout };
    double phase = 0;
    // Plays a sine for a second and returns the output's level relative to the input's, in dB (after 0.5 s of settling).
    auto Gain = [&](double frequency) {
        double inSum = 0, outSum = 0;
        for (int block = 0; block < 100; block++)
        {
            std::vector<float> original(Frames * Channels);
            for (UINT32 i = 0; i < Frames; i++)
            {
                float s = static_cast<float>(0.25 * std::sin(phase));
                phase += 2 * 3.14159265358979323846 * frequency / Rate;
                for (UINT32 c = 0; c < Channels; c++) original[i * Channels + c] = buffer[i * Channels + c] = s;
            }
            pin.u32ValidFrameCount = Frames; pin.u32BufferFlags = BUFFER_VALID;
            rt->APOProcess(1, pins, 1, pouts);
            if (block < 50) continue;
            for (UINT32 i = 0; i < Frames * Channels; i++) { inSum += original[i] * original[i]; outSum += buffer[i] * buffer[i]; }
        }
        return 10 * std::log10(outSum / inSum);
    };

    double atPeak = Gain(1000), below = Gain(100);
    printf("     peak 1 kHz +6 dB Q 1: %.2f dB at 1 kHz, %.2f dB at 100 Hz\n", atPeak, below);
    Check(std::fabs(atPeak - 6) < 0.1, "the 1 kHz peak adds 6 dB at 1 kHz");
    Check(std::fabs(below) < 0.5, "and leaves 100 Hz alone");

    // A change of the file while playing: picked up by the watcher within a second.
    WriteSettings("preamp -6\nlowshelf 120 4 0.7\n");
    Sleep(1200);
    double low = Gain(40), high = Gain(5000);
    printf("     preamp -6, low shelf +4 dB: %.2f dB at 40 Hz, %.2f dB at 5 kHz\n", low, high);
    Check(std::fabs(low - (-2)) < 0.3 && std::fabs(high - (-6)) < 0.2, "a changed file is applied while playing");

    WriteSettings("");
    Sleep(1200);
    Check(std::fabs(Gain(1000)) < 0.01, "an empty file is flat");
    Check(Status().rfind("applied:", 0) == 0, "the status file says what was applied");

    // Refusals keep what was running.
    WriteSettings("preamp 10\npeak 1000 20 1\n");
    Sleep(1200);
    printf("     status: %s", Status().c_str());
    Check(std::fabs(Gain(1000)) < 0.01 && Status().find("+24 dB") != std::string::npos, "a curve boosting over +24 dB is refused");
    WriteSettings("preamp 2\npeak 2022.7 30 30\n");
    Sleep(1200);
    Check(std::fabs(Gain(1000)) < 0.01 && Status().find("+24 dB") != std::string::npos, "a narrow +32 dB band between the check points is refused");
    if (PlantUserOwned("peak 1000 -6 1\n"))
    {
        Sleep(1200);
        printf("     status: %s", Status().c_str());
        Check(std::fabs(Gain(1000)) < 0.01 && Status().find("owned") != std::string::npos, "a file Administrators don't own is refused");
    }
    else Check(false, "planting a user-owned file (run elevated)");
    DeleteFileW(SettingsPath().c_str());
    std::wstring target = SettingsPath() + L".target";
    WriteSettings("peak 1000 -6 1\n", target.c_str());
    if (CreateSymbolicLinkW(SettingsPath().c_str(), target.c_str(), 0))
    {
        Sleep(1200);
        printf("     status: %s", Status().c_str());
        Check(std::fabs(Gain(1000)) < 0.01 && Status().find("link") != std::string::npos, "a link in place of the file is refused");
        DeleteFileW(SettingsPath().c_str());
    }
    else printf("SKIP a link in place of the file (no symbolic link privilege)\n");
    DeleteFileW(target.c_str());

    pin.u32BufferFlags = BUFFER_SILENT;
    rt->APOProcess(1, pins, 1, pouts);
    Check(pout.u32BufferFlags == BUFFER_SILENT, "silence stays flagged as silence");

    // Cost: ten bands on stereo 48 kHz, 10 ms buffers.
    WriteSettings("preamp -3\npeak 31 2 1.4\npeak 62 2 1.4\npeak 125 2 1.4\npeak 250 2 1.4\npeak 500 2 1.4\npeak 1000 2 1.4\npeak 2000 2 1.4\npeak 4000 2 1.4\npeak 8000 2 1.4\npeak 16000 2 1.4\n");
    Sleep(1200);
    pin.u32BufferFlags = BUFFER_VALID;
    auto start = std::chrono::steady_clock::now();
    for (int i = 0; i < 2000; i++) { pin.u32ValidFrameCount = Frames; pin.u32BufferFlags = BUFFER_VALID; rt->APOProcess(1, pins, 1, pouts); }
    double us = std::chrono::duration<double, std::micro>(std::chrono::steady_clock::now() - start).count() / 2000;
    printf("     10 bands, stereo, 480 frames: %.1f us per buffer (%.3f %% of the 10 ms it covers)\n", us, us / 100);
    bool finite = true;
    for (float v : buffer) finite &= std::isfinite(v) != 0;
    Check(finite && us < 1000, "ten bands cost well under the buffer's time, output finite");

    // A NaN in the input: that buffer goes silent and the filters start over instead of carrying it forever.
    double before = Gain(1000);
    for (auto& v : buffer) v = 0.1f;
    buffer[7] = NAN;
    pin.u32ValidFrameCount = Frames; pin.u32BufferFlags = BUFFER_VALID;
    rt->APOProcess(1, pins, 1, pouts);
    bool silent = true;
    for (float v : buffer) silent &= v == 0;
    Check(silent, "a NaN in the input silences that buffer");
    double after = Gain(1000);
    printf("     before the NaN %.2f dB, after %.2f dB at 1 kHz\n", before, after);
    Check(std::isfinite(after) && std::fabs(after - before) < 0.05, "and the next buffers play as before");

    Check(SUCCEEDED(config->UnlockForProcess()), "UnlockForProcess");
    rt->Release();
    config->Release();
    apo->Release();
    factory->Release();
    auto canUnload = reinterpret_cast<HRESULT(STDAPICALLTYPE*)()>(GetProcAddress(dll, "DllCanUnloadNow"));
    Check(canUnload && canUnload() == S_OK, "the DLL can unload once everything is released");

    DeleteFileW(SettingsPath().c_str());
    if (madeEq) RemoveDirectoryW(eq.c_str());
    if (madeAudio) RemoveDirectoryW(audio.c_str());
    printf("%d failure(s)\n", failures);
    return failures;
}
