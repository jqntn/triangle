using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Input.Glfw;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;

unsafe
{
    GlfwWindowing.RegisterPlatform();
    GlfwInput.RegisterPlatform();

    bool enableValidationLayers = false;
    string[] validationLayers = ["VK_LAYER_KHRONOS_validation"];

    IWindow? window;

    window = Window.Create(WindowOptions.DefaultVulkan);
    window.Initialize();

    ArgumentNullException.ThrowIfNull(window.VkSurface);

    Vk? vk = Vk.GetApi();

    if (enableValidationLayers)
    {
        Trace.Assert(CheckValidationLayerSupport());
    }

    ApplicationInfo applicationInfo = new()
    {
        SType = StructureType.ApplicationInfo,
        PApplicationName = (byte*)Marshal.StringToHGlobalAnsi("Triangle"),
        PEngineName = (byte*)Marshal.StringToHGlobalAnsi("Silk.NET"),
        ApplicationVersion = new Version32(1, 0, 0),
        EngineVersion = new Version32(1, 0, 0),
        ApiVersion = Vk.Version13,
    };

    InstanceCreateInfo instanceCreateInfo = new()
    {
        SType = StructureType.InstanceCreateInfo,
        PApplicationInfo = &applicationInfo,
    };

    string[] extensions = GetRequiredExtensions();
    instanceCreateInfo.EnabledExtensionCount = (uint)extensions.Length;
    instanceCreateInfo.PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions);

    DebugUtilsMessengerCreateInfoEXT debugUtilsMessengerCreateInfo = new()
    {
        SType = StructureType.DebugUtilsMessengerCreateInfoExt,
        MessageSeverity =
            DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt
            | DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
            | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
        MessageType =
            DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
            | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt
            | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt,
        PfnUserCallback = (DebugUtilsMessengerCallbackFunctionEXT)DebugCallback,
    };

    if (enableValidationLayers)
    {
        instanceCreateInfo.EnabledLayerCount = (uint)validationLayers.Length;
        instanceCreateInfo.PpEnabledLayerNames = (byte**)
            SilkMarshal.StringArrayToPtr(validationLayers);
        instanceCreateInfo.PNext = &debugUtilsMessengerCreateInfo;
    }

    Trace.Assert(
        vk.CreateInstance(in instanceCreateInfo, null, out Instance instance) == Result.Success
    );

    Marshal.FreeHGlobal((nint)applicationInfo.PApplicationName);
    Marshal.FreeHGlobal((nint)applicationInfo.PEngineName);

    Trace.Assert(SilkMarshal.Free((nint)instanceCreateInfo.PpEnabledExtensionNames));
    if (enableValidationLayers)
    {
        Trace.Assert(SilkMarshal.Free((nint)instanceCreateInfo.PpEnabledLayerNames));
    }

    ExtDebugUtils? debugUtils = null;
    DebugUtilsMessengerEXT debugUtilsMessenger = default;

    if (enableValidationLayers)
    {
        if (vk.TryGetInstanceExtension(instance, out debugUtils))
        {
            Trace.Assert(
                debugUtils!.CreateDebugUtilsMessenger(
                    instance,
                    in debugUtilsMessengerCreateInfo,
                    null,
                    out debugUtilsMessenger
                ) == Result.Success
            );
        }
    }

    window.Run();

    if (enableValidationLayers)
    {
        debugUtils!.DestroyDebugUtilsMessenger(instance, debugUtilsMessenger, null);
    }

    vk.DestroyInstance(instance, null);
    vk.Dispose();

    window.Dispose();

    bool CheckValidationLayerSupport()
    {
        uint layerCount = 0;
        Trace.Assert(vk.EnumerateInstanceLayerProperties(ref layerCount, null) == Result.Success);

        LayerProperties[] availableLayers = new LayerProperties[layerCount];
        fixed (LayerProperties* availableLayersPtr = availableLayers)
        {
            Trace.Assert(
                vk.EnumerateInstanceLayerProperties(ref layerCount, availableLayersPtr)
                    == Result.Success
            );
        }

        return validationLayers.All(
            availableLayers.Select(layer => Marshal.PtrToStringAnsi((nint)layer.LayerName)).Contains
        );
    }

    string[] GetRequiredExtensions()
    {
        byte** glfwExtensions = window.VkSurface.GetRequiredExtensions(out uint glfwExtensionCount);

        string[] extensions = SilkMarshal.PtrToStringArray(
            (nint)glfwExtensions,
            (int)glfwExtensionCount
        );

        return enableValidationLayers ? [.. extensions, ExtDebugUtils.ExtensionName] : extensions;
    }

    uint DebugCallback(
        DebugUtilsMessageSeverityFlagsEXT messageSeverity,
        DebugUtilsMessageTypeFlagsEXT messageTypes,
        DebugUtilsMessengerCallbackDataEXT* pCallbackData,
        void* pUserData
    )
    {
        Console.WriteLine(
            $"VALIDATION LAYER:" + Marshal.PtrToStringAnsi((nint)pCallbackData->PMessage)
        );

        return Vk.False;
    }
}
