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
using Triangle;
using Device = Silk.NET.Vulkan.Device;
using Semaphore = Silk.NET.Vulkan.Semaphore;

unsafe
{
    GlfwWindowing.RegisterPlatform();
    GlfwInput.RegisterPlatform();

    IWindow window = Window.Create(WindowOptions.DefaultVulkan);
    window.Initialize();
    ArgumentNullException.ThrowIfNull(window.VkSurface);

    bool enableValidationLayers = false;

    string[] validationLayers = ["VK_LAYER_KHRONOS_validation"];
    string[] physicalDeviceExtensions = [KhrSwapchain.ExtensionName];

    Vk vk = Vk.GetApi();

    if (enableValidationLayers)
    {
        Trace.Assert(CheckValidationLayersSupport());
    }

    #region Create Instance

    string[] extensions = GetRequiredExtensions();

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
        EnabledExtensionCount = (uint)extensions.Length,
        PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions),
    };

    DebugUtilsMessengerCreateInfoEXT debugMessengerCreateInfo = new()
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
        instanceCreateInfo.PNext = &debugMessengerCreateInfo;
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

    #endregion

    #region Create Debug Messenger

    ExtDebugUtils extDebugUtils = null!;
    DebugUtilsMessengerEXT debugMessenger = new();

    if (enableValidationLayers)
    {
        Trace.Assert(vk.TryGetInstanceExtension(instance, out extDebugUtils));
        Trace.Assert(
            extDebugUtils.CreateDebugUtilsMessenger(
                instance,
                in debugMessengerCreateInfo,
                null,
                out debugMessenger
            ) == Result.Success
        );
    }

    #endregion

    #region Create Surface

    Trace.Assert(vk.TryGetInstanceExtension(instance, out KhrSurface khrSurface));

    SurfaceKHR surface = window
        .VkSurface.Create<AllocationCallbacks>(instance.ToHandle(), null)
        .ToSurface();

    #endregion

    #region Get Physical Device

    PhysicalDevice physicalDevice = vk.GetPhysicalDevices(instance).First(IsSuitable);

    #endregion

    #region Create Logical Device

    Device device = CreateLogicalDevice(out Queue graphicsQueue, out Queue presentQueue);

    #endregion

    #region Create Swapchain

    Trace.Assert(vk.TryGetDeviceExtension(instance, device, out KhrSwapchain khrSwapchain));

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

    window.Render += DrawFrame;
    window.Run();
    Trace.Assert(vk.DeviceWaitIdle(device) == Result.Success);

    #endregion

    #region Clean Up

    for (int i = 0; i < maxFramesInFlight; i++)
    {
        vk.DestroySemaphore(device, renderFinishedSemaphores[i], null);
        vk.DestroySemaphore(device, imageAvailableSemaphores[i], null);
        vk.DestroyFence(device, inFlightFences[i], null);
    }

    vk.DestroyCommandPool(device, commandPool, null);

    foreach (Framebuffer framebuffer in swapchainFramebuffers)
    {
        vk.DestroyFramebuffer(device, framebuffer, null);
    }

    vk.DestroyPipeline(device, graphicsPipeline, null);
    vk.DestroyPipelineLayout(device, pipelineLayout, null);
    vk.DestroyRenderPass(device, renderPass, null);

    foreach (ImageView imageView in swapchainImageViews)
    {
        vk.DestroyImageView(device, imageView, null);
    }

    khrSwapchain.DestroySwapchain(device, swapchain, null);

    vk.DestroyDevice(device, null);

    if (enableValidationLayers)
    {
        extDebugUtils.DestroyDebugUtilsMessenger(instance, debugMessenger, null);
    }

    khrSurface.DestroySurface(instance, surface, null);
    vk.DestroyInstance(instance, null);
    vk.Dispose();

    window.Dispose();

    #endregion

    bool CheckValidationLayersSupport()
    {
        uint availableLayersCount = 0;
        Trace.Assert(
            vk.EnumerateInstanceLayerProperties(ref availableLayersCount, null) == Result.Success
        );

        LayerProperties[] availableLayers = new LayerProperties[availableLayersCount];
        fixed (LayerProperties* availableLayersPtr = availableLayers)
        {
            Trace.Assert(
                vk.EnumerateInstanceLayerProperties(ref availableLayersCount, availableLayersPtr)
                    == Result.Success
            );
        }

        return validationLayers.All(
            availableLayers.Select(x => Marshal.PtrToStringAnsi((nint)x.LayerName)).Contains
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
            $"VALIDATION LAYER: {Marshal.PtrToStringAnsi((nint)pCallbackData->PMessage)}"
        );

        return Vk.False;
    }

    bool IsSuitable(PhysicalDevice physicalDevice)
    {
        if (!FindQueueFamilies(physicalDevice).IsComplete)
        {
            return false;
        }

        if (!CheckExtensionsSupport(physicalDevice))
        {
            return false;
        }

        SwapchainSupportDetails swapchainSupportDetails = QuerySwapchainSupport(physicalDevice);

        bool isSwapchainAdequate =
            swapchainSupportDetails.Formats.Length != 0
            && swapchainSupportDetails.PresentModes.Length != 0;

        return isSwapchainAdequate;
    }

    QueueFamilyIndices FindQueueFamilies(PhysicalDevice physicalDevice)
    {
        uint queueFamiliesCount = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, ref queueFamiliesCount, null);

        QueueFamilyProperties[] queueFamilies = new QueueFamilyProperties[queueFamiliesCount];
        fixed (QueueFamilyProperties* queueFamiliesPtr = queueFamilies)
        {
            vk.GetPhysicalDeviceQueueFamilyProperties(
                physicalDevice,
                ref queueFamiliesCount,
                queueFamiliesPtr
            );
        }

        QueueFamilyIndices queueFamilyIndices = new();

        for (uint i = 0; i < queueFamilies.Length; i++)
        {
            if (queueFamilies[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
            {
                queueFamilyIndices.GraphicsFamilyIndex ??= i;
            }

            Trace.Assert(
                khrSurface.GetPhysicalDeviceSurfaceSupport(
                    physicalDevice,
                    i,
                    surface,
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

    bool CheckExtensionsSupport(PhysicalDevice physicalDevice)
    {
        uint availableExtensionsCount = 0;
        Trace.Assert(
            vk.EnumerateDeviceExtensionProperties(
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
                vk.EnumerateDeviceExtensionProperties(
                    physicalDevice,
                    (byte*)null,
                    ref availableExtensionsCount,
                    availableExtensionsPtr
                ) == Result.Success
            );
        }

        return physicalDeviceExtensions.All(
            availableExtensions.Select(x => Marshal.PtrToStringAnsi((nint)x.ExtensionName)).Contains
        );
    }

    SwapchainSupportDetails QuerySwapchainSupport(PhysicalDevice physicalDevice)
    {
        SwapchainSupportDetails swapchainSupportDetails = new();

        Trace.Assert(
            khrSurface.GetPhysicalDeviceSurfaceCapabilities(
                physicalDevice,
                surface,
                out SurfaceCapabilitiesKHR capabilities
            ) == Result.Success
        );
        swapchainSupportDetails.Capabilities = capabilities;

        uint surfaceFormatsCount = 0;
        Trace.Assert(
            khrSurface.GetPhysicalDeviceSurfaceFormats(
                physicalDevice,
                surface,
                ref surfaceFormatsCount,
                null
            ) == Result.Success
        );

        if (surfaceFormatsCount != 0)
        {
            swapchainSupportDetails.Formats = new SurfaceFormatKHR[surfaceFormatsCount];
            fixed (SurfaceFormatKHR* surfaceFormatsPtr = swapchainSupportDetails.Formats)
            {
                Trace.Assert(
                    khrSurface.GetPhysicalDeviceSurfaceFormats(
                        physicalDevice,
                        surface,
                        ref surfaceFormatsCount,
                        surfaceFormatsPtr
                    ) == Result.Success
                );
            }
        }

        uint surfacePresentModesCount = 0;
        Trace.Assert(
            khrSurface.GetPhysicalDeviceSurfacePresentModes(
                physicalDevice,
                surface,
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
                    khrSurface.GetPhysicalDeviceSurfacePresentModes(
                        physicalDevice,
                        surface,
                        ref surfacePresentModesCount,
                        surfacePresentModesPtr
                    ) == Result.Success
                );
            }
        }

        return swapchainSupportDetails;
    }

    Device CreateLogicalDevice(out Queue graphicsQueue, out Queue presentQueue)
    {
        QueueFamilyIndices queueFamilyIndices = FindQueueFamilies(physicalDevice);

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
            EnabledExtensionCount = (uint)physicalDeviceExtensions.Length,
            PpEnabledExtensionNames = (byte**)
                SilkMarshal.StringArrayToPtr(physicalDeviceExtensions),
        };

        if (enableValidationLayers)
        {
            deviceCreateInfo.EnabledLayerCount = (uint)validationLayers.Length;
            deviceCreateInfo.PpEnabledLayerNames = (byte**)
                SilkMarshal.StringArrayToPtr(validationLayers);
        }

        Trace.Assert(
            vk.CreateDevice(physicalDevice, in deviceCreateInfo, null, out Device device)
                == Result.Success
        );

        if (enableValidationLayers)
        {
            Trace.Assert(SilkMarshal.Free((nint)deviceCreateInfo.PpEnabledLayerNames));
        }

        Trace.Assert(SilkMarshal.Free((nint)deviceCreateInfo.PpEnabledExtensionNames));

        vk.GetDeviceQueue(
            device,
            queueFamilyIndices.GraphicsFamilyIndex!.Value,
            0,
            out graphicsQueue
        );
        vk.GetDeviceQueue(
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
        SwapchainSupportDetails swapchainSupportDetails = QuerySwapchainSupport(physicalDevice);

        SurfaceFormatKHR surfaceFormat = ChooseSwapchainSurfaceFormat(
            swapchainSupportDetails.Formats
        );
        swapchainImageFormat = surfaceFormat.Format;

        PresentModeKHR surfacePresentMode = ChooseSwapchainPresentMode(
            swapchainSupportDetails.PresentModes
        );

        swapchainExtent = ChooseSwapchainExtent(swapchainSupportDetails.Capabilities);

        uint swapchainImagesCount = swapchainSupportDetails.Capabilities.MinImageCount + 1;
        if (
            swapchainSupportDetails.Capabilities.MaxImageCount > 0
            && swapchainImagesCount > swapchainSupportDetails.Capabilities.MaxImageCount
        )
        {
            swapchainImagesCount = swapchainSupportDetails.Capabilities.MaxImageCount;
        }

        SwapchainCreateInfoKHR swapchainCreateInfo = new()
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = surface,
            MinImageCount = swapchainImagesCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = swapchainExtent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit,
            PreTransform = swapchainSupportDetails.Capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = surfacePresentMode,
            Clipped = true,
        };

        QueueFamilyIndices queueFamilyIndices = FindQueueFamilies(physicalDevice);
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

    SurfaceFormatKHR ChooseSwapchainSurfaceFormat(IReadOnlyList<SurfaceFormatKHR> surfaceFormats)
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
            Vector2D<int> framebufferSize = window.FramebufferSize;

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
                vk.CreateImageView(device, in imageViewCreateInfo, null, out swapchainImageViews[i])
                    == Result.Success
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
            vk.CreateRenderPass(device, in renderPassCreateInfo, null, out RenderPass renderPass)
                == Result.Success
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
            vk.CreatePipelineLayout(device, in pipelineLayoutCreateInfo, null, out pipelineLayout)
                == Result.Success
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
            vk.CreateGraphicsPipelines(
                device,
                default,
                1,
                in graphicsPipelineCreateInfo,
                null,
                out Pipeline graphicsPipeline
            ) == Result.Success
        );

        vk.DestroyShaderModule(device, fragShaderModule, null);
        vk.DestroyShaderModule(device, vertShaderModule, null);

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
                vk.CreateShaderModule(device, in shaderModuleCreateInfo, null, out shaderModule)
                    == Result.Success
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
                vk.CreateFramebuffer(
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
        QueueFamilyIndices queueFamilyIndices = FindQueueFamilies(physicalDevice);

        CommandPoolCreateInfo commandPoolCreateInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = queueFamilyIndices.GraphicsFamilyIndex!.Value,
        };

        Trace.Assert(
            vk.CreateCommandPool(
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
                vk.AllocateCommandBuffers(device, in commandBufferAllocateInfo, commandBuffersPtr)
                    == Result.Success
            );
        }

        for (int i = 0; i < commandBuffers.Length; i++)
        {
            CommandBufferBeginInfo commandBufferBeginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
            };

            Trace.Assert(
                vk.BeginCommandBuffer(commandBuffers[i], in commandBufferBeginInfo)
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

            vk.CmdBeginRenderPass(commandBuffers[i], &renderPassBeginInfo, SubpassContents.Inline);

            vk.CmdBindPipeline(commandBuffers[i], PipelineBindPoint.Graphics, graphicsPipeline);
            vk.CmdDraw(commandBuffers[i], 3, 1, 0, 0);

            vk.CmdEndRenderPass(commandBuffers[i]);

            Trace.Assert(vk.EndCommandBuffer(commandBuffers[i]) == Result.Success);
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
                vk.CreateSemaphore(
                    device,
                    in semaphoreCreateInfo,
                    null,
                    out imageAvailableSemaphores[i]
                ) == Result.Success
            );

            Trace.Assert(
                vk.CreateSemaphore(
                    device,
                    in semaphoreCreateInfo,
                    null,
                    out renderFinishedSemaphores[i]
                ) == Result.Success
            );

            Trace.Assert(
                vk.CreateFence(device, in fenceCreateInfo, null, out inFlightFences[i])
                    == Result.Success
            );
        }
    }

    void DrawFrame(double delta)
    {
        Trace.Assert(
            vk.WaitForFences(device, 1, in inFlightFences[currentFrame], true, ulong.MaxValue)
                == Result.Success
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
                vk.WaitForFences(device, 1, in imagesInFlight[imageIndex], true, ulong.MaxValue)
                    == Result.Success
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

        Trace.Assert(vk.ResetFences(device, 1, in inFlightFences[currentFrame]) == Result.Success);
        Trace.Assert(
            vk.QueueSubmit(graphicsQueue, 1, in submitInfo, inFlightFences[currentFrame])
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

internal struct QueueFamilyIndices
{
    public uint? GraphicsFamilyIndex { get; set; }
    public uint? PresentFamilyIndex { get; set; }

    public readonly bool IsComplete => GraphicsFamilyIndex.HasValue && PresentFamilyIndex.HasValue;
}

internal struct SwapchainSupportDetails
{
    public SurfaceCapabilitiesKHR Capabilities { get; set; }
    public SurfaceFormatKHR[] Formats { get; set; } = [];
    public PresentModeKHR[] PresentModes { get; set; } = [];

    public SwapchainSupportDetails() { }
}
