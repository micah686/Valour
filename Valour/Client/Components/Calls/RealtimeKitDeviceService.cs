using Microsoft.JSInterop;

namespace Valour.Client.Components.Calls;

public sealed class RealtimeKitDeviceService : IAsyncDisposable
{
    private const string ModulePath = "./_content/Valour.Client/Components/Calls/RealtimeKitComponent.razor.js";

    private readonly IJSRuntime _jsRuntime;
    private Task<IJSObjectReference>? _moduleTask;

    public RealtimeKitDeviceService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    private async Task<IJSObjectReference> GetModuleAsync()
    {
        _moduleTask ??= _jsRuntime.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();
        return await _moduleTask;
    }

    public async Task<InputMic[]> GetAudioInputDevicesAsync()
    {
        var module = await GetModuleAsync();
        return await module.InvokeAsync<InputMic[]>("getAudioInputDevices");
    }

    public async Task<string> GetMicrophonePermissionStateAsync()
    {
        var module = await GetModuleAsync();
        return await module.InvokeAsync<string>("getMicrophonePermissionState");
    }

    public async Task<bool> RequestMicrophonePermissionAsync()
    {
        var module = await GetModuleAsync();
        return await module.InvokeAsync<bool>("requestMicrophonePermission");
    }

    public async ValueTask DisposeAsync()
    {
        if (_moduleTask is null || !_moduleTask.IsCompletedSuccessfully)
            return;

        try
        {
            var module = await _moduleTask;
            await module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // Ignore if runtime already disconnected.
        }
    }
}
