using System.Diagnostics;
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

    private const int VK_MAX_FRAMES_IN_FLIGHT = 2;

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
    private static VkQueueFamilyIndices s_vkQueueFamilyIndices;

    private static Device s_vkDevice;
    private static Queue s_vkGraphicsQueue;
    private static Queue s_vkPresentQueue;

    private static KhrSwapchain s_vkKhrSwapchain = null!;
    private static SwapchainKHR s_vkSwapchain;
    private static Format s_vkSwapchainImageFormat;
    private static Extent2D s_vkSwapchainExtent;
    private static Image[] s_vkSwapchainImages = null!;

    private static ImageView[] s_vkSwapchainImageViews = null!;

    private static RenderPass s_vkRenderPass;
    private static Pipeline s_vkGraphicsPipeline;
    private static PipelineLayout s_vkPipelineLayout;

    private static Framebuffer[] s_vkSwapchainFramebuffers = null!;

    private static CommandPool s_vkCommandPool;
    private static CommandBuffer[] s_vkCommandBuffers = null!;

    private static Semaphore[] s_vkImageAvailableSemaphores = null!;
    private static Semaphore[] s_vkRenderFinishedSemaphores = null!;
    private static Fence[] s_vkInFlightFences = null!;
    private static Fence[] s_vkImagesInFlight = null!;

    private static int s_vkCurrentFrame;

    private static void Main()
    {
        Init();
        Run();
        Cleanup();
    }

    private static void Init()
    {
        RegisterGlfw();
        InitWindow();
        InitVulkan();
    }

    private static void Run()
    {
        s_window.Render += DrawFrame;
        s_window.Run();
        Trace.Assert(s_vkApi.DeviceWaitIdle(s_vkDevice) == Result.Success);
    }

    private static void Cleanup()
    {
        for (int i = 0; i < VK_MAX_FRAMES_IN_FLIGHT; i++)
        {
            s_vkApi.DestroySemaphore(s_vkDevice, s_vkRenderFinishedSemaphores[i], null);
            s_vkApi.DestroySemaphore(s_vkDevice, s_vkImageAvailableSemaphores[i], null);
            s_vkApi.DestroyFence(s_vkDevice, s_vkInFlightFences[i], null);
        }

        s_vkApi.DestroyCommandPool(s_vkDevice, s_vkCommandPool, null);

        foreach (Framebuffer framebuffer in s_vkSwapchainFramebuffers)
        {
            s_vkApi.DestroyFramebuffer(s_vkDevice, framebuffer, null);
        }

        s_vkApi.DestroyPipeline(s_vkDevice, s_vkGraphicsPipeline, null);
        s_vkApi.DestroyPipelineLayout(s_vkDevice, s_vkPipelineLayout, null);
        s_vkApi.DestroyRenderPass(s_vkDevice, s_vkRenderPass, null);

        foreach (ImageView imageView in s_vkSwapchainImageViews)
        {
            s_vkApi.DestroyImageView(s_vkDevice, imageView, null);
        }

        s_vkKhrSwapchain.DestroySwapchain(s_vkDevice, s_vkSwapchain, null);

        s_vkApi.DestroyDevice(s_vkDevice, null);

        if (s_vkEnableValidationLayers)
        {
            s_vkExtDebugUtils.DestroyDebugUtilsMessenger(s_vkInstance, s_vkDebugMessenger, null);
        }

        s_vkKhrSurface.DestroySurface(s_vkInstance, s_vkSurface, null);
        s_vkApi.DestroyInstance(s_vkInstance, null);
        s_vkApi.Dispose();

        s_window.Dispose();
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
        VkCreateLogicalDevice();
        VkCreateSwapchain();
        VkCreateSwapchainImageViews();
        VkCreateRenderPass();
        VkCreateGraphicsPipeline();
        VkCreateSwapchainFramebuffers();
        VkCreateCommandPool();
        VkCreateCommandBuffers();
        VkCreateSynchronizationObjects();
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

    private static void VkCreateLogicalDevice()
    {
        uint[] uniqueQueueFamilies =
        [
            s_vkQueueFamilyIndices.GraphicsFamilyIndex!.Value,
            s_vkQueueFamilyIndices.PresentFamilyIndex!.Value,
        ];
        uniqueQueueFamilies = [.. uniqueQueueFamilies.Distinct()];

        DeviceQueueCreateInfo* deviceQueueCreateInfos =
            stackalloc DeviceQueueCreateInfo[
                uniqueQueueFamilies.Length * sizeof(DeviceQueueCreateInfo)
            ];

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
            s_vkApi.CreateDevice(s_vkPhysicalDevice, in deviceCreateInfo, null, out s_vkDevice)
                == Result.Success
        );

        if (s_vkEnableValidationLayers)
        {
            Trace.Assert(SilkMarshal.Free((nint)deviceCreateInfo.PpEnabledLayerNames));
        }

        Trace.Assert(SilkMarshal.Free((nint)deviceCreateInfo.PpEnabledExtensionNames));

        s_vkApi.GetDeviceQueue(
            s_vkDevice,
            s_vkQueueFamilyIndices.GraphicsFamilyIndex!.Value,
            0,
            out s_vkGraphicsQueue
        );
        s_vkApi.GetDeviceQueue(
            s_vkDevice,
            s_vkQueueFamilyIndices.PresentFamilyIndex!.Value,
            0,
            out s_vkPresentQueue
        );
    }

    private static void VkCreateSwapchain()
    {
        Trace.Assert(s_vkApi.TryGetDeviceExtension(s_vkInstance, s_vkDevice, out s_vkKhrSwapchain));

        VkSwapchainSupportDetails swapchainSupportDetails = VkQuerySwapchainSupport(
            s_vkPhysicalDevice
        );

        SurfaceFormatKHR surfaceFormat = VkChooseSwapchainSurfaceFormat(
            swapchainSupportDetails.SurfaceFormats
        );
        s_vkSwapchainImageFormat = surfaceFormat.Format;

        PresentModeKHR surfacePresentMode = VkChooseSwapchainPresentMode(
            swapchainSupportDetails.PresentModes
        );

        s_vkSwapchainExtent = VkChooseSwapchainExtent(swapchainSupportDetails.SurfaceCapabilities);

        uint swapchainImagesCount = swapchainSupportDetails.SurfaceCapabilities.MinImageCount + 1;
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
            ImageExtent = s_vkSwapchainExtent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit,
            PreTransform = swapchainSupportDetails.SurfaceCapabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = surfacePresentMode,
            Clipped = true,
        };

        uint* queueFamilyIndicesPtr = stackalloc[] {
            s_vkQueueFamilyIndices.GraphicsFamilyIndex!.Value,
            s_vkQueueFamilyIndices.PresentFamilyIndex!.Value,
        };

        if (s_vkQueueFamilyIndices.GraphicsFamilyIndex != s_vkQueueFamilyIndices.PresentFamilyIndex)
        {
            swapchainCreateInfo.ImageSharingMode = SharingMode.Concurrent;
            swapchainCreateInfo.PQueueFamilyIndices = queueFamilyIndicesPtr;
            swapchainCreateInfo.QueueFamilyIndexCount = 2;
        }

        Trace.Assert(
            s_vkKhrSwapchain.CreateSwapchain(
                s_vkDevice,
                in swapchainCreateInfo,
                null,
                out s_vkSwapchain
            ) == Result.Success
        );

        Trace.Assert(
            s_vkKhrSwapchain.GetSwapchainImages(
                s_vkDevice,
                s_vkSwapchain,
                ref swapchainImagesCount,
                null
            ) == Result.Success
        );
        s_vkSwapchainImages = new Image[swapchainImagesCount];
        fixed (Image* swapchainImagesPtr = s_vkSwapchainImages)
        {
            Trace.Assert(
                s_vkKhrSwapchain.GetSwapchainImages(
                    s_vkDevice,
                    s_vkSwapchain,
                    ref swapchainImagesCount,
                    swapchainImagesPtr
                ) == Result.Success
            );
        }
    }

    private static void VkCreateSwapchainImageViews()
    {
        s_vkSwapchainImageViews = new ImageView[s_vkSwapchainImages.Length];

        for (int i = 0; i < s_vkSwapchainImages.Length; i++)
        {
            ImageViewCreateInfo imageViewCreateInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = s_vkSwapchainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = s_vkSwapchainImageFormat,
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
                    s_vkDevice,
                    in imageViewCreateInfo,
                    null,
                    out s_vkSwapchainImageViews[i]
                ) == Result.Success
            );
        }
    }

    private static void VkCreateRenderPass()
    {
        AttachmentDescription colorAttachmentDescription = new()
        {
            Format = s_vkSwapchainImageFormat,
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
            s_vkApi.CreateRenderPass(s_vkDevice, in renderPassCreateInfo, null, out s_vkRenderPass)
                == Result.Success
        );
    }

    private static void VkCreateGraphicsPipeline()
    {
        byte[] vertShaderCode = CompiledShaders.shaders.shader_vert.ToArray();
        byte[] fragShaderCode = CompiledShaders.shaders.shader_frag.ToArray();

        ShaderModule vertShaderModule = VkCreateShaderModule(vertShaderCode);
        ShaderModule fragShaderModule = VkCreateShaderModule(fragShaderCode);

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
            Width = s_vkSwapchainExtent.Width,
            Height = s_vkSwapchainExtent.Height,
            MinDepth = 0,
            MaxDepth = 1,
        };

        Rect2D scissor = new() { Offset = { X = 0, Y = 0 }, Extent = s_vkSwapchainExtent };

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
                s_vkDevice,
                in pipelineLayoutCreateInfo,
                null,
                out s_vkPipelineLayout
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
            Layout = s_vkPipelineLayout,
            RenderPass = s_vkRenderPass,
            Subpass = 0,
        };

        Trace.Assert(
            s_vkApi.CreateGraphicsPipelines(
                s_vkDevice,
                default,
                1,
                in graphicsPipelineCreateInfo,
                null,
                out s_vkGraphicsPipeline
            ) == Result.Success
        );

        s_vkApi.DestroyShaderModule(s_vkDevice, fragShaderModule, null);
        s_vkApi.DestroyShaderModule(s_vkDevice, vertShaderModule, null);

        Trace.Assert(SilkMarshal.Free((nint)vertShaderStageCreateInfo.PName));
        Trace.Assert(SilkMarshal.Free((nint)fragShaderStageCreateInfo.PName));
    }

    private static void VkCreateSwapchainFramebuffers()
    {
        s_vkSwapchainFramebuffers = new Framebuffer[s_vkSwapchainImageViews.Length];

        for (int i = 0; i < s_vkSwapchainImageViews.Length; i++)
        {
            ImageView attachment = s_vkSwapchainImageViews[i];

            FramebufferCreateInfo framebufferCreateInfo = new()
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = s_vkRenderPass,
                AttachmentCount = 1,
                PAttachments = &attachment,
                Width = s_vkSwapchainExtent.Width,
                Height = s_vkSwapchainExtent.Height,
                Layers = 1,
            };

            Trace.Assert(
                s_vkApi.CreateFramebuffer(
                    s_vkDevice,
                    in framebufferCreateInfo,
                    null,
                    out s_vkSwapchainFramebuffers[i]
                ) == Result.Success
            );
        }
    }

    private static void VkCreateCommandPool()
    {
        CommandPoolCreateInfo commandPoolCreateInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = s_vkQueueFamilyIndices.GraphicsFamilyIndex!.Value,
        };

        Trace.Assert(
            s_vkApi.CreateCommandPool(
                s_vkDevice,
                in commandPoolCreateInfo,
                null,
                out s_vkCommandPool
            ) == Result.Success
        );
    }

    private static void VkCreateCommandBuffers()
    {
        s_vkCommandBuffers = new CommandBuffer[s_vkSwapchainFramebuffers.Length];

        CommandBufferAllocateInfo commandBufferAllocateInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = s_vkCommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = (uint)s_vkCommandBuffers.Length,
        };

        fixed (CommandBuffer* commandBuffersPtr = s_vkCommandBuffers)
        {
            Trace.Assert(
                s_vkApi.AllocateCommandBuffers(
                    s_vkDevice,
                    in commandBufferAllocateInfo,
                    commandBuffersPtr
                ) == Result.Success
            );
        }

        for (int i = 0; i < s_vkCommandBuffers.Length; i++)
        {
            CommandBufferBeginInfo commandBufferBeginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
            };

            Trace.Assert(
                s_vkApi.BeginCommandBuffer(s_vkCommandBuffers[i], in commandBufferBeginInfo)
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
                RenderPass = s_vkRenderPass,
                Framebuffer = s_vkSwapchainFramebuffers[i],
                RenderArea = { Offset = { X = 0, Y = 0 }, Extent = s_vkSwapchainExtent },
                ClearValueCount = 1,
                PClearValues = &clearValue,
            };

            s_vkApi.CmdBeginRenderPass(
                s_vkCommandBuffers[i],
                &renderPassBeginInfo,
                SubpassContents.Inline
            );

            s_vkApi.CmdBindPipeline(
                s_vkCommandBuffers[i],
                PipelineBindPoint.Graphics,
                s_vkGraphicsPipeline
            );
            s_vkApi.CmdDraw(s_vkCommandBuffers[i], 3, 1, 0, 0);

            s_vkApi.CmdEndRenderPass(s_vkCommandBuffers[i]);

            Trace.Assert(s_vkApi.EndCommandBuffer(s_vkCommandBuffers[i]) == Result.Success);
        }
    }

    private static void VkCreateSynchronizationObjects()
    {
        s_vkImageAvailableSemaphores = new Semaphore[VK_MAX_FRAMES_IN_FLIGHT];
        s_vkRenderFinishedSemaphores = new Semaphore[VK_MAX_FRAMES_IN_FLIGHT];

        s_vkInFlightFences = new Fence[VK_MAX_FRAMES_IN_FLIGHT];
        s_vkImagesInFlight = new Fence[s_vkSwapchainImages.Length];

        SemaphoreCreateInfo semaphoreCreateInfo = new()
        {
            SType = StructureType.SemaphoreCreateInfo,
        };

        FenceCreateInfo fenceCreateInfo = new()
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit,
        };

        for (int i = 0; i < VK_MAX_FRAMES_IN_FLIGHT; i++)
        {
            Trace.Assert(
                s_vkApi.CreateSemaphore(
                    s_vkDevice,
                    in semaphoreCreateInfo,
                    null,
                    out s_vkImageAvailableSemaphores[i]
                ) == Result.Success
            );

            Trace.Assert(
                s_vkApi.CreateSemaphore(
                    s_vkDevice,
                    in semaphoreCreateInfo,
                    null,
                    out s_vkRenderFinishedSemaphores[i]
                ) == Result.Success
            );

            Trace.Assert(
                s_vkApi.CreateFence(s_vkDevice, in fenceCreateInfo, null, out s_vkInFlightFences[i])
                    == Result.Success
            );
        }
    }

    private static void DrawFrame(double delta)
    {
        Trace.Assert(
            s_vkApi.WaitForFences(
                s_vkDevice,
                1,
                in s_vkInFlightFences[s_vkCurrentFrame],
                true,
                ulong.MaxValue
            ) == Result.Success
        );

        uint imageIndex = 0;
        Trace.Assert(
            s_vkKhrSwapchain.AcquireNextImage(
                s_vkDevice,
                s_vkSwapchain,
                ulong.MaxValue,
                s_vkImageAvailableSemaphores[s_vkCurrentFrame],
                default,
                ref imageIndex
            ) == Result.Success
        );

        if (s_vkImagesInFlight[imageIndex].Handle != 0)
        {
            Trace.Assert(
                s_vkApi.WaitForFences(
                    s_vkDevice,
                    1,
                    in s_vkImagesInFlight[imageIndex],
                    true,
                    ulong.MaxValue
                ) == Result.Success
            );
        }

        s_vkImagesInFlight[imageIndex] = s_vkInFlightFences[s_vkCurrentFrame];

        Semaphore* waitSemaphores = stackalloc[] { s_vkImageAvailableSemaphores[s_vkCurrentFrame] };
        PipelineStageFlags* waitStages = stackalloc[] {
            PipelineStageFlags.ColorAttachmentOutputBit,
        };
        CommandBuffer commandBuffer = s_vkCommandBuffers[imageIndex];
        Semaphore* signalSemaphores = stackalloc[] {
            s_vkRenderFinishedSemaphores[s_vkCurrentFrame],
        };
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
            s_vkApi.ResetFences(s_vkDevice, 1, in s_vkInFlightFences[s_vkCurrentFrame])
                == Result.Success
        );
        Trace.Assert(
            s_vkApi.QueueSubmit(
                s_vkGraphicsQueue,
                1,
                in submitInfo,
                s_vkInFlightFences[s_vkCurrentFrame]
            ) == Result.Success
        );

        SwapchainKHR* swapchains = stackalloc[] { s_vkSwapchain };
        PresentInfoKHR presentInfo = new()
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = signalSemaphores,
            SwapchainCount = 1,
            PSwapchains = swapchains,
            PImageIndices = &imageIndex,
        };

        Trace.Assert(
            s_vkKhrSwapchain.QueuePresent(s_vkPresentQueue, in presentInfo) == Result.Success
        );

        s_vkCurrentFrame = (s_vkCurrentFrame + 1) % VK_MAX_FRAMES_IN_FLIGHT;
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
        s_vkQueueFamilyIndices = VkFindQueueFamilies(physicalDevice);

        if (!s_vkQueueFamilyIndices.IsComplete)
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

    private static SurfaceFormatKHR VkChooseSwapchainSurfaceFormat(
        SurfaceFormatKHR[] surfaceFormats
    )
    {
        return surfaceFormats.FirstOrDefault(
            x =>
                x.Format == Format.B8G8R8A8Srgb
                && x.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr,
            surfaceFormats[0]
        );
    }

    private static PresentModeKHR VkChooseSwapchainPresentMode(
        IReadOnlyList<PresentModeKHR> presentModes
    )
    {
        return presentModes.FirstOrDefault(
            x => x == PresentModeKHR.MailboxKhr,
            PresentModeKHR.FifoKhr
        );
    }

    private static Extent2D VkChooseSwapchainExtent(SurfaceCapabilitiesKHR surfaceCapabilities)
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

    private static ShaderModule VkCreateShaderModule(byte[] code)
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
                    s_vkDevice,
                    in shaderModuleCreateInfo,
                    null,
                    out shaderModule
                ) == Result.Success
            );
        }

        return shaderModule;
    }
}
