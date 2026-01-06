using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Input.Glfw;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;
using Device = Silk.NET.Vulkan.Device;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Triangle;

internal static unsafe class Program
{
    private struct VkQueueFamilyIndices
    {
        public uint? GraphicsFamilyIndex { get; set; }
        public uint? PresentFamilyIndex { get; set; }

        public readonly bool IsComplete =>
            GraphicsFamilyIndex.HasValue && PresentFamilyIndex.HasValue;
    }

    private struct VkSwapchainSupportDetails
    {
        public SurfaceCapabilitiesKHR SurfaceCapabilities { get; set; }
        public SurfaceFormatKHR[] SurfaceFormats { get; set; } = [];
        public PresentModeKHR[] PresentModes { get; set; } = [];

        public VkSwapchainSupportDetails() { }
    }

    private static readonly bool s_vkEnableValidationLayers = false;

    private static readonly string[] s_vkRequiredValidationLayers = ["VK_LAYER_KHRONOS_validation"];
    private static readonly string[] s_vkRequiredPhysicalDeviceExtensions =
    [
        KhrSwapchain.ExtensionName,
    ];

    private static IWindow s_window = null!;

    private static Vk s_vkApi = null!;

    private static Instance s_vkInstance;

    private static ExtDebugUtils s_vkExtDebugUtils = null!;
    private static DebugUtilsMessengerEXT s_vkDebugMessenger;
    private static DebugUtilsMessengerCreateInfoEXT s_vkDebugMessengerCreateInfo;

    private static KhrSurface s_vkKhrSurface = null!;
    private static SurfaceKHR s_vkSurface;

    private static PhysicalDevice s_vkPhysicalDevice;

    private static void Init()
    {
        RegisterGlfw();
        InitWindow();
        InitVulkan();
    }

    private static void RegisterGlfw()
    {
        GlfwWindowing.RegisterPlatform();
        GlfwInput.RegisterPlatform();
    }

    private static void InitWindow()
    {
        s_window = Window.Create(WindowOptions.DefaultVulkan);
        s_window.Initialize();
        ArgumentNullException.ThrowIfNull(s_window.VkSurface);
    }

    private static void InitVulkan()
    {
        VkInitApi();
        VkPopulateDebugMessengerCreateInfo();
        VkCreateInstance();
        VkCreateDebugMessenger();
        VkCreateSurface();
        VkFindPhysicalDevice();
    }

    private static void VkInitApi()
    {
        s_vkApi = Vk.GetApi();

        if (s_vkEnableValidationLayers)
        {
            Trace.Assert(VkCheckValidationLayersSupport());
        }
    }

    private static void VkPopulateDebugMessengerCreateInfo()
    {
        s_vkDebugMessengerCreateInfo.SType = StructureType.DebugUtilsMessengerCreateInfoExt;
        s_vkDebugMessengerCreateInfo.MessageSeverity =
            DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt
            | DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
            | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt;
        s_vkDebugMessengerCreateInfo.MessageType =
            DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
            | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt
            | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt;
        s_vkDebugMessengerCreateInfo.PfnUserCallback =
            (DebugUtilsMessengerCallbackFunctionEXT)VkDebugCallback;
    }

    private static void VkCreateInstance()
    {
        string[] enabledExtensions = VkGetInstanceRequiredExtensions();

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
            EnabledExtensionCount = (uint)enabledExtensions.Length,
            PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(enabledExtensions),
        };

        DebugUtilsMessengerCreateInfoEXT debugMessengerCreateInfo = s_vkDebugMessengerCreateInfo;

        if (s_vkEnableValidationLayers)
        {
            instanceCreateInfo.EnabledLayerCount = (uint)s_vkRequiredValidationLayers.Length;
            instanceCreateInfo.PpEnabledLayerNames = (byte**)
                SilkMarshal.StringArrayToPtr(s_vkRequiredValidationLayers);
            instanceCreateInfo.PNext = &debugMessengerCreateInfo;
        }

        Trace.Assert(
            s_vkApi.CreateInstance(in instanceCreateInfo, null, out s_vkInstance) == Result.Success
        );

        Marshal.FreeHGlobal((nint)applicationInfo.PApplicationName);
        Marshal.FreeHGlobal((nint)applicationInfo.PEngineName);

        Trace.Assert(SilkMarshal.Free((nint)instanceCreateInfo.PpEnabledExtensionNames));
        if (s_vkEnableValidationLayers)
        {
            Trace.Assert(SilkMarshal.Free((nint)instanceCreateInfo.PpEnabledLayerNames));
        }
    }

    private static void VkCreateDebugMessenger()
    {
        DebugUtilsMessengerCreateInfoEXT debugMessengerCreateInfo = s_vkDebugMessengerCreateInfo;

        if (s_vkEnableValidationLayers)
        {
            Trace.Assert(s_vkApi.TryGetInstanceExtension(s_vkInstance, out s_vkExtDebugUtils));
            Trace.Assert(
                s_vkExtDebugUtils.CreateDebugUtilsMessenger(
                    s_vkInstance,
                    in debugMessengerCreateInfo,
                    null,
                    out s_vkDebugMessenger
                ) == Result.Success
            );
        }
    }

    private static void VkCreateSurface()
    {
        Trace.Assert(s_vkApi.TryGetInstanceExtension(s_vkInstance, out s_vkKhrSurface));

        s_vkSurface = s_window
            .VkSurface!.Create<AllocationCallbacks>(s_vkInstance.ToHandle(), null)
            .ToSurface();
    }

    private static void VkFindPhysicalDevice()
    {
        s_vkPhysicalDevice = s_vkApi
            .GetPhysicalDevices(s_vkInstance)
            .First(VkIsPhysicalDeviceSuitable);
    }

    private static bool VkCheckValidationLayersSupport()
    {
        uint availableLayersCount = 0;
        Trace.Assert(
            s_vkApi.EnumerateInstanceLayerProperties(ref availableLayersCount, null)
                == Result.Success
        );

        LayerProperties[] availableLayers = new LayerProperties[availableLayersCount];
        fixed (LayerProperties* availableLayersPtr = availableLayers)
        {
            Trace.Assert(
                s_vkApi.EnumerateInstanceLayerProperties(
                    ref availableLayersCount,
                    availableLayersPtr
                ) == Result.Success
            );
        }

        return s_vkRequiredValidationLayers.All(
            availableLayers.Select(x => Marshal.PtrToStringAnsi((nint)x.LayerName)).Contains
        );
    }

    private static string[] VkGetInstanceRequiredExtensions()
    {
        byte** glfwExtensionsPtr = s_window.VkSurface!.GetRequiredExtensions(
            out uint glfwExtensionsCount
        );

        string[] glfwExtensions = SilkMarshal.PtrToStringArray(
            (nint)glfwExtensionsPtr,
            (int)glfwExtensionsCount
        );

        return s_vkEnableValidationLayers
            ? [.. glfwExtensions, ExtDebugUtils.ExtensionName]
            : glfwExtensions;
    }

    private static uint VkDebugCallback(
        DebugUtilsMessageSeverityFlagsEXT messageSeverity,
        DebugUtilsMessageTypeFlagsEXT messageTypes,
        DebugUtilsMessengerCallbackDataEXT* pCallbackData,
        void* pUserData
    )
    {
        Console.WriteLine(
            $"VULKAN VALIDATION LAYER: {Marshal.PtrToStringAnsi((nint)pCallbackData->PMessage)}"
        );

        return Vk.False;
    }

    private static bool VkIsPhysicalDeviceSuitable(PhysicalDevice physicalDevice)
    {
        if (!VkFindQueueFamilies(physicalDevice).IsComplete)
        {
            return false;
        }

        if (!VkCheckPhysicalDeviceExtensionsSupport(physicalDevice))
        {
            return false;
        }

        VkSwapchainSupportDetails swapchainSupportDetails = VkQuerySwapchainSupport(physicalDevice);

        bool isSwapchainAdequate =
            swapchainSupportDetails.SurfaceFormats.Length != 0
            && swapchainSupportDetails.PresentModes.Length != 0;

        return isSwapchainAdequate;
    }

    private static VkQueueFamilyIndices VkFindQueueFamilies(PhysicalDevice physicalDevice)
    {
        uint queueFamiliesCount = 0;
        s_vkApi.GetPhysicalDeviceQueueFamilyProperties(
            physicalDevice,
            ref queueFamiliesCount,
            null
        );

        QueueFamilyProperties[] queueFamilies = new QueueFamilyProperties[queueFamiliesCount];
        fixed (QueueFamilyProperties* queueFamiliesPtr = queueFamilies)
        {
            s_vkApi.GetPhysicalDeviceQueueFamilyProperties(
                physicalDevice,
                ref queueFamiliesCount,
                queueFamiliesPtr
            );
        }

        VkQueueFamilyIndices queueFamilyIndices = new();

        for (uint i = 0; i < queueFamilies.Length; i++)
        {
            if (queueFamilies[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
            {
                queueFamilyIndices.GraphicsFamilyIndex ??= i;
            }

            Trace.Assert(
                s_vkKhrSurface.GetPhysicalDeviceSurfaceSupport(
                    physicalDevice,
                    i,
                    s_vkSurface,
                    out Bool32 presentSupport
                ) == Result.Success
            );

            if (presentSupport)
            {
                queueFamilyIndices.PresentFamilyIndex ??= i;
            }

            if (queueFamilyIndices.IsComplete)
            {
                break;
            }
        }

        return queueFamilyIndices;
    }

    private static bool VkCheckPhysicalDeviceExtensionsSupport(PhysicalDevice physicalDevice)
    {
        uint availableExtensionsCount = 0;
        Trace.Assert(
            s_vkApi.EnumerateDeviceExtensionProperties(
                physicalDevice,
                (byte*)null,
                ref availableExtensionsCount,
                null
            ) == Result.Success
        );

        ExtensionProperties[] availableExtensions = new ExtensionProperties[
            availableExtensionsCount
        ];
        fixed (ExtensionProperties* availableExtensionsPtr = availableExtensions)
        {
            Trace.Assert(
                s_vkApi.EnumerateDeviceExtensionProperties(
                    physicalDevice,
                    (byte*)null,
                    ref availableExtensionsCount,
                    availableExtensionsPtr
                ) == Result.Success
            );
        }

        return s_vkRequiredPhysicalDeviceExtensions.All(
            availableExtensions.Select(x => Marshal.PtrToStringAnsi((nint)x.ExtensionName)).Contains
        );
    }

    private static VkSwapchainSupportDetails VkQuerySwapchainSupport(PhysicalDevice physicalDevice)
    {
        VkSwapchainSupportDetails swapchainSupportDetails = new();

        Trace.Assert(
            s_vkKhrSurface.GetPhysicalDeviceSurfaceCapabilities(
                physicalDevice,
                s_vkSurface,
                out SurfaceCapabilitiesKHR capabilities
            ) == Result.Success
        );
        swapchainSupportDetails.SurfaceCapabilities = capabilities;

        uint surfaceFormatsCount = 0;
        Trace.Assert(
            s_vkKhrSurface.GetPhysicalDeviceSurfaceFormats(
                physicalDevice,
                s_vkSurface,
                ref surfaceFormatsCount,
                null
            ) == Result.Success
        );

        if (surfaceFormatsCount != 0)
        {
            swapchainSupportDetails.SurfaceFormats = new SurfaceFormatKHR[surfaceFormatsCount];
            fixed (SurfaceFormatKHR* surfaceFormatsPtr = swapchainSupportDetails.SurfaceFormats)
            {
                Trace.Assert(
                    s_vkKhrSurface.GetPhysicalDeviceSurfaceFormats(
                        physicalDevice,
                        s_vkSurface,
                        ref surfaceFormatsCount,
                        surfaceFormatsPtr
                    ) == Result.Success
                );
            }
        }

        uint surfacePresentModesCount = 0;
        Trace.Assert(
            s_vkKhrSurface.GetPhysicalDeviceSurfacePresentModes(
                physicalDevice,
                s_vkSurface,
                ref surfacePresentModesCount,
                null
            ) == Result.Success
        );

        if (surfacePresentModesCount != 0)
        {
            swapchainSupportDetails.PresentModes = new PresentModeKHR[surfacePresentModesCount];
            fixed (PresentModeKHR* surfacePresentModesPtr = swapchainSupportDetails.PresentModes)
            {
                Trace.Assert(
                    s_vkKhrSurface.GetPhysicalDeviceSurfacePresentModes(
                        physicalDevice,
                        s_vkSurface,
                        ref surfacePresentModesCount,
                        surfacePresentModesPtr
                    ) == Result.Success
                );
            }
        }

        return swapchainSupportDetails;
    }

    private static void Main()
    {
        Init();

        #region Create Logical Device

        Device device = CreateLogicalDevice(out Queue graphicsQueue, out Queue presentQueue);

        #endregion

        #region Create Swapchain

        Trace.Assert(
            s_vkApi.TryGetDeviceExtension(s_vkInstance, device, out KhrSwapchain khrSwapchain)
        );

        SwapchainKHR swapchain = CreateSwapchain(
            out Image[] swapchainImages,
            out Format swapchainImageFormat,
            out Extent2D swapchainExtent
        );

        ImageView[] swapchainImageViews = CreateSwapchainImageViews();

        #endregion

        #region Create Graphics Pipeline

        RenderPass renderPass = CreateRenderPass();
        Pipeline graphicsPipeline = CreateGraphicsPipeline(out PipelineLayout pipelineLayout);

        #endregion

        #region Create Swapchain Framebuffers

        Framebuffer[] swapchainFramebuffers = CreateSwapchainFramebuffers();

        #endregion

        #region Create Command Buffers

        CommandPool commandPool = CreateCommandPool();
        CommandBuffer[] commandBuffers = CreateCommandBuffers();

        #endregion

        #region Create Synchronization Objects

        int maxFramesInFlight = 2;

        CreateSynchronizationObjects(
            out Semaphore[] imageAvailableSemaphores,
            out Semaphore[] renderFinishedSemaphores,
            out Fence[] inFlightFences,
            out Fence[] imagesInFlight
        );

        int currentFrame = 0;

        #endregion

        #region Main Loop

        s_window.Render += DrawFrame;
        s_window.Run();
        Trace.Assert(s_vkApi.DeviceWaitIdle(device) == Result.Success);

        #endregion

        #region Clean Up

        for (int i = 0; i < maxFramesInFlight; i++)
        {
            s_vkApi.DestroySemaphore(device, renderFinishedSemaphores[i], null);
            s_vkApi.DestroySemaphore(device, imageAvailableSemaphores[i], null);
            s_vkApi.DestroyFence(device, inFlightFences[i], null);
        }

        s_vkApi.DestroyCommandPool(device, commandPool, null);

        foreach (Framebuffer framebuffer in swapchainFramebuffers)
        {
            s_vkApi.DestroyFramebuffer(device, framebuffer, null);
        }

        s_vkApi.DestroyPipeline(device, graphicsPipeline, null);
        s_vkApi.DestroyPipelineLayout(device, pipelineLayout, null);
        s_vkApi.DestroyRenderPass(device, renderPass, null);

        foreach (ImageView imageView in swapchainImageViews)
        {
            s_vkApi.DestroyImageView(device, imageView, null);
        }

        khrSwapchain.DestroySwapchain(device, swapchain, null);

        s_vkApi.DestroyDevice(device, null);

        if (s_vkEnableValidationLayers)
        {
            s_vkExtDebugUtils.DestroyDebugUtilsMessenger(s_vkInstance, s_vkDebugMessenger, null);
        }

        s_vkKhrSurface.DestroySurface(s_vkInstance, s_vkSurface, null);
        s_vkApi.DestroyInstance(s_vkInstance, null);
        s_vkApi.Dispose();

        s_window.Dispose();

        #endregion

        Device CreateLogicalDevice(out Queue graphicsQueue, out Queue presentQueue)
        {
            VkQueueFamilyIndices queueFamilyIndices = VkFindQueueFamilies(s_vkPhysicalDevice);

            uint[] uniqueQueueFamilies =
            [
                queueFamilyIndices.GraphicsFamilyIndex!.Value,
                queueFamilyIndices.PresentFamilyIndex!.Value,
            ];
            uniqueQueueFamilies = [.. uniqueQueueFamilies.Distinct()];

            using GlobalMemory deviceQueueCreateInfosAlloc = GlobalMemory.Allocate(
                uniqueQueueFamilies.Length * sizeof(DeviceQueueCreateInfo)
            );
            DeviceQueueCreateInfo* deviceQueueCreateInfos = (DeviceQueueCreateInfo*)
                Unsafe.AsPointer(ref deviceQueueCreateInfosAlloc.GetPinnableReference());

            float queuePriority = 1.0f;
            for (int i = 0; i < uniqueQueueFamilies.Length; i++)
            {
                deviceQueueCreateInfos[i] = new()
                {
                    SType = StructureType.DeviceQueueCreateInfo,
                    QueueFamilyIndex = uniqueQueueFamilies[i],
                    PQueuePriorities = &queuePriority,
                    QueueCount = 1,
                };
            }

            PhysicalDeviceFeatures physicalDeviceFeatures = new();
            DeviceCreateInfo deviceCreateInfo = new()
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = (uint)uniqueQueueFamilies.Length,
                PQueueCreateInfos = deviceQueueCreateInfos,
                PEnabledFeatures = &physicalDeviceFeatures,
                EnabledExtensionCount = (uint)s_vkRequiredPhysicalDeviceExtensions.Length,
                PpEnabledExtensionNames = (byte**)
                    SilkMarshal.StringArrayToPtr(s_vkRequiredPhysicalDeviceExtensions),
            };

            if (s_vkEnableValidationLayers)
            {
                deviceCreateInfo.EnabledLayerCount = (uint)s_vkRequiredValidationLayers.Length;
                deviceCreateInfo.PpEnabledLayerNames = (byte**)
                    SilkMarshal.StringArrayToPtr(s_vkRequiredValidationLayers);
            }

            Trace.Assert(
                s_vkApi.CreateDevice(
                    s_vkPhysicalDevice,
                    in deviceCreateInfo,
                    null,
                    out Device device
                ) == Result.Success
            );

            if (s_vkEnableValidationLayers)
            {
                Trace.Assert(SilkMarshal.Free((nint)deviceCreateInfo.PpEnabledLayerNames));
            }

            Trace.Assert(SilkMarshal.Free((nint)deviceCreateInfo.PpEnabledExtensionNames));

            s_vkApi.GetDeviceQueue(
                device,
                queueFamilyIndices.GraphicsFamilyIndex!.Value,
                0,
                out graphicsQueue
            );
            s_vkApi.GetDeviceQueue(
                device,
                queueFamilyIndices.PresentFamilyIndex!.Value,
                0,
                out presentQueue
            );

            return device;
        }

        SwapchainKHR CreateSwapchain(
            out Image[] swapchainImages,
            out Format swapchainImageFormat,
            out Extent2D swapchainExtent
        )
        {
            VkSwapchainSupportDetails swapchainSupportDetails = VkQuerySwapchainSupport(
                s_vkPhysicalDevice
            );

            SurfaceFormatKHR surfaceFormat = ChooseSwapchainSurfaceFormat(
                swapchainSupportDetails.SurfaceFormats
            );
            swapchainImageFormat = surfaceFormat.Format;

            PresentModeKHR surfacePresentMode = ChooseSwapchainPresentMode(
                swapchainSupportDetails.PresentModes
            );

            swapchainExtent = ChooseSwapchainExtent(swapchainSupportDetails.SurfaceCapabilities);

            uint swapchainImagesCount =
                swapchainSupportDetails.SurfaceCapabilities.MinImageCount + 1;
            if (
                swapchainSupportDetails.SurfaceCapabilities.MaxImageCount > 0
                && swapchainImagesCount > swapchainSupportDetails.SurfaceCapabilities.MaxImageCount
            )
            {
                swapchainImagesCount = swapchainSupportDetails.SurfaceCapabilities.MaxImageCount;
            }

            SwapchainCreateInfoKHR swapchainCreateInfo = new()
            {
                SType = StructureType.SwapchainCreateInfoKhr,
                Surface = s_vkSurface,
                MinImageCount = swapchainImagesCount,
                ImageFormat = surfaceFormat.Format,
                ImageColorSpace = surfaceFormat.ColorSpace,
                ImageExtent = swapchainExtent,
                ImageArrayLayers = 1,
                ImageUsage = ImageUsageFlags.ColorAttachmentBit,
                PreTransform = swapchainSupportDetails.SurfaceCapabilities.CurrentTransform,
                CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
                PresentMode = surfacePresentMode,
                Clipped = true,
            };

            VkQueueFamilyIndices queueFamilyIndices = VkFindQueueFamilies(s_vkPhysicalDevice);
            uint* queueFamilyIndicesPtr = stackalloc[] {
                queueFamilyIndices.GraphicsFamilyIndex!.Value,
                queueFamilyIndices.PresentFamilyIndex!.Value,
            };

            if (queueFamilyIndices.GraphicsFamilyIndex != queueFamilyIndices.PresentFamilyIndex)
            {
                swapchainCreateInfo.ImageSharingMode = SharingMode.Concurrent;
                swapchainCreateInfo.PQueueFamilyIndices = queueFamilyIndicesPtr;
                swapchainCreateInfo.QueueFamilyIndexCount = 2;
            }

            Trace.Assert(
                khrSwapchain.CreateSwapchain(
                    device,
                    in swapchainCreateInfo,
                    null,
                    out SwapchainKHR swapchain
                ) == Result.Success
            );

            Trace.Assert(
                khrSwapchain.GetSwapchainImages(device, swapchain, ref swapchainImagesCount, null)
                    == Result.Success
            );
            swapchainImages = new Image[swapchainImagesCount];
            fixed (Image* swapchainImagesPtr = swapchainImages)
            {
                Trace.Assert(
                    khrSwapchain.GetSwapchainImages(
                        device,
                        swapchain,
                        ref swapchainImagesCount,
                        swapchainImagesPtr
                    ) == Result.Success
                );
            }

            return swapchain;
        }

        SurfaceFormatKHR ChooseSwapchainSurfaceFormat(
            IReadOnlyList<SurfaceFormatKHR> surfaceFormats
        )
        {
            return surfaceFormats.FirstOrDefault(
                x =>
                    x.Format == Format.B8G8R8A8Srgb
                    && x.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr,
                surfaceFormats[0]
            );
        }

        PresentModeKHR ChooseSwapchainPresentMode(IReadOnlyList<PresentModeKHR> presentModes)
        {
            return presentModes.FirstOrDefault(
                x => x == PresentModeKHR.MailboxKhr,
                PresentModeKHR.FifoKhr
            );
        }

        Extent2D ChooseSwapchainExtent(SurfaceCapabilitiesKHR surfaceCapabilities)
        {
            if (surfaceCapabilities.CurrentExtent.Width != uint.MaxValue)
            {
                return surfaceCapabilities.CurrentExtent;
            }
            else
            {
                Vector2D<int> framebufferSize = s_window.FramebufferSize;

                Extent2D actualExtent = new()
                {
                    Width = (uint)framebufferSize.X,
                    Height = (uint)framebufferSize.Y,
                };

                actualExtent.Width = Math.Clamp(
                    actualExtent.Width,
                    surfaceCapabilities.MinImageExtent.Width,
                    surfaceCapabilities.MaxImageExtent.Width
                );
                actualExtent.Height = Math.Clamp(
                    actualExtent.Height,
                    surfaceCapabilities.MinImageExtent.Height,
                    surfaceCapabilities.MaxImageExtent.Height
                );

                return actualExtent;
            }
        }

        ImageView[] CreateSwapchainImageViews()
        {
            ImageView[] swapchainImageViews = new ImageView[swapchainImages.Length];

            for (int i = 0; i < swapchainImages.Length; i++)
            {
                ImageViewCreateInfo imageViewCreateInfo = new()
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = swapchainImages[i],
                    ViewType = ImageViewType.Type2D,
                    Format = swapchainImageFormat,
                    Components =
                    {
                        R = ComponentSwizzle.Identity,
                        G = ComponentSwizzle.Identity,
                        B = ComponentSwizzle.Identity,
                        A = ComponentSwizzle.Identity,
                    },
                    SubresourceRange =
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        BaseMipLevel = 0,
                        LevelCount = 1,
                        BaseArrayLayer = 0,
                        LayerCount = 1,
                    },
                };

                Trace.Assert(
                    s_vkApi.CreateImageView(
                        device,
                        in imageViewCreateInfo,
                        null,
                        out swapchainImageViews[i]
                    ) == Result.Success
                );
            }

            return swapchainImageViews;
        }

        RenderPass CreateRenderPass()
        {
            AttachmentDescription colorAttachmentDescription = new()
            {
                Format = swapchainImageFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.PresentSrcKhr,
            };

            AttachmentReference colorAttachmentDescriptionRef = new()
            {
                Attachment = 0,
                Layout = ImageLayout.ColorAttachmentOptimal,
            };

            SubpassDescription subpassDescription = new()
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAttachmentDescriptionRef,
            };

            RenderPassCreateInfo renderPassCreateInfo = new()
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = 1,
                PAttachments = &colorAttachmentDescription,
                SubpassCount = 1,
                PSubpasses = &subpassDescription,
            };

            Trace.Assert(
                s_vkApi.CreateRenderPass(
                    device,
                    in renderPassCreateInfo,
                    null,
                    out RenderPass renderPass
                ) == Result.Success
            );

            return renderPass;
        }

        Pipeline CreateGraphicsPipeline(out PipelineLayout pipelineLayout)
        {
            byte[] vertShaderCode = CompiledShaders.shaders.shader_vert.ToArray();
            byte[] fragShaderCode = CompiledShaders.shaders.shader_frag.ToArray();

            ShaderModule vertShaderModule = CreateShaderModule(vertShaderCode);
            ShaderModule fragShaderModule = CreateShaderModule(fragShaderCode);

            PipelineShaderStageCreateInfo vertShaderStageCreateInfo = new()
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertShaderModule,
                PName = (byte*)SilkMarshal.StringToPtr("main"),
            };

            PipelineShaderStageCreateInfo fragShaderStageCreateInfo = new()
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragShaderModule,
                PName = (byte*)SilkMarshal.StringToPtr("main"),
            };

            PipelineShaderStageCreateInfo* shaderStageCreateInfos = stackalloc[] {
                vertShaderStageCreateInfo,
                fragShaderStageCreateInfo,
            };

            PipelineVertexInputStateCreateInfo vertexInputStateCreateInfo = new()
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
            };

            PipelineInputAssemblyStateCreateInfo inputAssemblyStateCreateInfo = new()
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };

            Viewport viewport = new()
            {
                X = 0,
                Y = 0,
                Width = swapchainExtent.Width,
                Height = swapchainExtent.Height,
                MinDepth = 0,
                MaxDepth = 1,
            };

            Rect2D scissor = new() { Offset = { X = 0, Y = 0 }, Extent = swapchainExtent };

            PipelineViewportStateCreateInfo viewportStateCreateInfo = new()
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                PViewports = &viewport,
                ScissorCount = 1,
                PScissors = &scissor,
            };

            PipelineRasterizationStateCreateInfo rasterizationStateCreateInfo = new()
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                LineWidth = 1,
                CullMode = CullModeFlags.BackBit,
                FrontFace = FrontFace.Clockwise,
            };

            PipelineMultisampleStateCreateInfo multisampleStateCreateInfo = new()
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };

            PipelineColorBlendAttachmentState colorBlendAttachmentState = new()
            {
                ColorWriteMask =
                    ColorComponentFlags.RBit
                    | ColorComponentFlags.GBit
                    | ColorComponentFlags.BBit
                    | ColorComponentFlags.ABit,
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.Zero,
                AlphaBlendOp = BlendOp.Add,
            };

            PipelineColorBlendStateCreateInfo colorBlendStateCreateInfo = new()
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &colorBlendAttachmentState,
            };

            PipelineLayoutCreateInfo pipelineLayoutCreateInfo = new()
            {
                SType = StructureType.PipelineLayoutCreateInfo,
            };

            Trace.Assert(
                s_vkApi.CreatePipelineLayout(
                    device,
                    in pipelineLayoutCreateInfo,
                    null,
                    out pipelineLayout
                ) == Result.Success
            );

            GraphicsPipelineCreateInfo graphicsPipelineCreateInfo = new()
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = shaderStageCreateInfos,
                PVertexInputState = &vertexInputStateCreateInfo,
                PInputAssemblyState = &inputAssemblyStateCreateInfo,
                PViewportState = &viewportStateCreateInfo,
                PRasterizationState = &rasterizationStateCreateInfo,
                PMultisampleState = &multisampleStateCreateInfo,
                PColorBlendState = &colorBlendStateCreateInfo,
                Layout = pipelineLayout,
                RenderPass = renderPass,
                Subpass = 0,
            };

            Trace.Assert(
                s_vkApi.CreateGraphicsPipelines(
                    device,
                    default,
                    1,
                    in graphicsPipelineCreateInfo,
                    null,
                    out Pipeline graphicsPipeline
                ) == Result.Success
            );

            s_vkApi.DestroyShaderModule(device, fragShaderModule, null);
            s_vkApi.DestroyShaderModule(device, vertShaderModule, null);

            Trace.Assert(SilkMarshal.Free((nint)vertShaderStageCreateInfo.PName));
            Trace.Assert(SilkMarshal.Free((nint)fragShaderStageCreateInfo.PName));

            return graphicsPipeline;
        }

        ShaderModule CreateShaderModule(byte[] code)
        {
            ShaderModuleCreateInfo shaderModuleCreateInfo = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
            };

            ShaderModule shaderModule;

            fixed (byte* codePtr = code)
            {
                shaderModuleCreateInfo.PCode = (uint*)codePtr;

                Trace.Assert(
                    s_vkApi.CreateShaderModule(
                        device,
                        in shaderModuleCreateInfo,
                        null,
                        out shaderModule
                    ) == Result.Success
                );
            }

            return shaderModule;
        }

        Framebuffer[] CreateSwapchainFramebuffers()
        {
            swapchainFramebuffers = new Framebuffer[swapchainImageViews.Length];

            for (int i = 0; i < swapchainImageViews.Length; i++)
            {
                ImageView attachment = swapchainImageViews[i];

                FramebufferCreateInfo framebufferCreateInfo = new()
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = renderPass,
                    AttachmentCount = 1,
                    PAttachments = &attachment,
                    Width = swapchainExtent.Width,
                    Height = swapchainExtent.Height,
                    Layers = 1,
                };

                Trace.Assert(
                    s_vkApi.CreateFramebuffer(
                        device,
                        in framebufferCreateInfo,
                        null,
                        out swapchainFramebuffers[i]
                    ) == Result.Success
                );
            }

            return swapchainFramebuffers;
        }

        CommandPool CreateCommandPool()
        {
            VkQueueFamilyIndices queueFamilyIndices = VkFindQueueFamilies(s_vkPhysicalDevice);

            CommandPoolCreateInfo commandPoolCreateInfo = new()
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = queueFamilyIndices.GraphicsFamilyIndex!.Value,
            };

            Trace.Assert(
                s_vkApi.CreateCommandPool(
                    device,
                    in commandPoolCreateInfo,
                    null,
                    out CommandPool commandPool
                ) == Result.Success
            );

            return commandPool;
        }

        CommandBuffer[] CreateCommandBuffers()
        {
            CommandBuffer[] commandBuffers = new CommandBuffer[swapchainFramebuffers.Length];

            CommandBufferAllocateInfo commandBufferAllocateInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = (uint)commandBuffers.Length,
            };

            fixed (CommandBuffer* commandBuffersPtr = commandBuffers)
            {
                Trace.Assert(
                    s_vkApi.AllocateCommandBuffers(
                        device,
                        in commandBufferAllocateInfo,
                        commandBuffersPtr
                    ) == Result.Success
                );
            }

            for (int i = 0; i < commandBuffers.Length; i++)
            {
                CommandBufferBeginInfo commandBufferBeginInfo = new()
                {
                    SType = StructureType.CommandBufferBeginInfo,
                };

                Trace.Assert(
                    s_vkApi.BeginCommandBuffer(commandBuffers[i], in commandBufferBeginInfo)
                        == Result.Success
                );

                ClearValue clearValue = new()
                {
                    Color = new()
                    {
                        Float32_0 = 0,
                        Float32_1 = 0,
                        Float32_2 = 0,
                        Float32_3 = 1,
                    },
                };

                RenderPassBeginInfo renderPassBeginInfo = new()
                {
                    SType = StructureType.RenderPassBeginInfo,
                    RenderPass = renderPass,
                    Framebuffer = swapchainFramebuffers[i],
                    RenderArea = { Offset = { X = 0, Y = 0 }, Extent = swapchainExtent },
                    ClearValueCount = 1,
                    PClearValues = &clearValue,
                };

                s_vkApi.CmdBeginRenderPass(
                    commandBuffers[i],
                    &renderPassBeginInfo,
                    SubpassContents.Inline
                );

                s_vkApi.CmdBindPipeline(
                    commandBuffers[i],
                    PipelineBindPoint.Graphics,
                    graphicsPipeline
                );
                s_vkApi.CmdDraw(commandBuffers[i], 3, 1, 0, 0);

                s_vkApi.CmdEndRenderPass(commandBuffers[i]);

                Trace.Assert(s_vkApi.EndCommandBuffer(commandBuffers[i]) == Result.Success);
            }

            return commandBuffers;
        }

        void CreateSynchronizationObjects(
            out Semaphore[] imageAvailableSemaphores,
            out Semaphore[] renderFinishedSemaphores,
            out Fence[] inFlightFences,
            out Fence[] imagesInFlight
        )
        {
            imageAvailableSemaphores = new Semaphore[maxFramesInFlight];
            renderFinishedSemaphores = new Semaphore[maxFramesInFlight];

            inFlightFences = new Fence[maxFramesInFlight];
            imagesInFlight = new Fence[swapchainImages.Length];

            SemaphoreCreateInfo semaphoreCreateInfo = new()
            {
                SType = StructureType.SemaphoreCreateInfo,
            };

            FenceCreateInfo fenceCreateInfo = new()
            {
                SType = StructureType.FenceCreateInfo,
                Flags = FenceCreateFlags.SignaledBit,
            };

            for (int i = 0; i < maxFramesInFlight; i++)
            {
                Trace.Assert(
                    s_vkApi.CreateSemaphore(
                        device,
                        in semaphoreCreateInfo,
                        null,
                        out imageAvailableSemaphores[i]
                    ) == Result.Success
                );

                Trace.Assert(
                    s_vkApi.CreateSemaphore(
                        device,
                        in semaphoreCreateInfo,
                        null,
                        out renderFinishedSemaphores[i]
                    ) == Result.Success
                );

                Trace.Assert(
                    s_vkApi.CreateFence(device, in fenceCreateInfo, null, out inFlightFences[i])
                        == Result.Success
                );
            }
        }

        void DrawFrame(double delta)
        {
            Trace.Assert(
                s_vkApi.WaitForFences(
                    device,
                    1,
                    in inFlightFences[currentFrame],
                    true,
                    ulong.MaxValue
                ) == Result.Success
            );

            uint imageIndex = 0;
            Trace.Assert(
                khrSwapchain.AcquireNextImage(
                    device,
                    swapchain,
                    ulong.MaxValue,
                    imageAvailableSemaphores[currentFrame],
                    default,
                    ref imageIndex
                ) == Result.Success
            );

            if (imagesInFlight[imageIndex].Handle != 0)
            {
                Trace.Assert(
                    s_vkApi.WaitForFences(
                        device,
                        1,
                        in imagesInFlight[imageIndex],
                        true,
                        ulong.MaxValue
                    ) == Result.Success
                );
            }

            imagesInFlight[imageIndex] = inFlightFences[currentFrame];

            Semaphore* waitSemaphores = stackalloc[] { imageAvailableSemaphores[currentFrame] };
            PipelineStageFlags* waitStages = stackalloc[] {
                PipelineStageFlags.ColorAttachmentOutputBit,
            };
            CommandBuffer commandBuffer = commandBuffers[imageIndex];
            Semaphore* signalSemaphores = stackalloc[] { renderFinishedSemaphores[currentFrame] };
            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = waitSemaphores,
                PWaitDstStageMask = waitStages,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = signalSemaphores,
            };

            Trace.Assert(
                s_vkApi.ResetFences(device, 1, in inFlightFences[currentFrame]) == Result.Success
            );
            Trace.Assert(
                s_vkApi.QueueSubmit(graphicsQueue, 1, in submitInfo, inFlightFences[currentFrame])
                    == Result.Success
            );

            SwapchainKHR* swapchains = stackalloc[] { swapchain };
            PresentInfoKHR presentInfo = new()
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = signalSemaphores,
                SwapchainCount = 1,
                PSwapchains = swapchains,
                PImageIndices = &imageIndex,
            };

            Trace.Assert(khrSwapchain.QueuePresent(presentQueue, in presentInfo) == Result.Success);

            currentFrame = (currentFrame + 1) % maxFramesInFlight;
        }
    }
}
