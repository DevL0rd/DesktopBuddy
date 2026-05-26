using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using UnityEngine;

namespace DesktopBuddySharedTextureBridge
{
    internal sealed unsafe class DxvkDmaBufImporter : IDisposable
    {
        private const uint VK_FORMAT_B8G8R8A8_UNORM = 44;
        private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
        private const uint DXGI_FORMAT_B8G8R8X8_UNORM = 88;
        private const uint DRM_FORMAT_XRGB8888 = 0x34325258;
        private const uint D3D11_BIND_SHADER_RESOURCE = 0x8;
        private const uint D3D11_BIND_RENDER_TARGET = 0x20;
        private const uint D3D11_SRV_DIMENSION_TEXTURE2D = 4;
        private const uint VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT = 0x200;
        private const uint VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT = 0x1;
        private const uint VK_IMAGE_USAGE_TRANSFER_SRC_BIT = 0x1;
        private const uint VK_IMAGE_USAGE_TRANSFER_DST_BIT = 0x2;
        private const uint VK_IMAGE_USAGE_SAMPLED_BIT = 0x4;
        private const uint VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT = 0x10;
        private const int VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO = 1000072000;
        private const int VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO = 14;
        private const int VK_STRUCTURE_TYPE_IMAGE_DRM_FORMAT_MODIFIER_EXPLICIT_CREATE_INFO_EXT = 1000158004;
        private const int VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO = 5;
        private const int VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO = 1000127001;
        private const int VK_STRUCTURE_TYPE_IMPORT_MEMORY_FD_INFO_KHR = 1000074000;
        private const int VK_STRUCTURE_TYPE_MEMORY_FD_PROPERTIES_KHR = 1000074001;
        private const int VK_IMAGE_TYPE_2D = 1;
        private const int VK_IMAGE_TILING_OPTIMAL = 0;
        private const int VK_IMAGE_TILING_DRM_FORMAT_MODIFIER_EXT = 1000158000;
        private const int VK_SAMPLE_COUNT_1_BIT = 1;
        private const int VK_SHARING_MODE_EXCLUSIVE = 0;
        private const int VK_IMAGE_LAYOUT_UNDEFINED = 0;
        private const int ID3D11Device_CreateShaderResourceView = 7;

        private static readonly Guid DxvkInteropDevice1Guid = new Guid("e2ef5fa5-dc21-4af7-90c4-f67ef6a09324");

        private readonly ManualLogSource _log;
        private IntPtr _interop;
        private IntPtr _vkInstance;
        private IntPtr _vkPhysicalDevice;
        private IntPtr _vkDevice;
        private IntPtr _vulkanModule;
        private bool _initialized;

        private GetVulkanHandlesDelegate _getVulkanHandles;
        private CreateTexture2DFromVkImageDelegate _createTexture2DFromVkImage;
        private VkGetInstanceProcAddrDelegate _vkGetInstanceProcAddr;
        private VkGetDeviceProcAddrDelegate _vkGetDeviceProcAddr;
        private VkGetPhysicalDeviceMemoryPropertiesDelegate _vkGetPhysicalDeviceMemoryProperties;
        private VkCreateImageDelegate _vkCreateImage;
        private VkDestroyImageDelegate _vkDestroyImage;
        private VkGetImageMemoryRequirementsDelegate _vkGetImageMemoryRequirements;
        private VkAllocateMemoryDelegate _vkAllocateMemory;
        private VkFreeMemoryDelegate _vkFreeMemory;
        private VkBindImageMemoryDelegate _vkBindImageMemory;
        private VkGetMemoryFdPropertiesKHRDelegate _vkGetMemoryFdPropertiesKHR;

        internal DxvkDmaBufImporter(ManualLogSource log)
        {
            _log = log;
        }

        internal bool EnsureInitialized()
        {
            if (_initialized) return true;
            if (!UnityD3D11Device.IsReady && !UnityD3D11Device.Initialize(_log))
                return false;

            Guid interopGuid = DxvkInteropDevice1Guid;
            int hr = Marshal.QueryInterface(UnityD3D11Device.D3dDevice, ref interopGuid, out _interop);
            if (hr < 0 || _interop == IntPtr.Zero)
            {
                SharedTextureBridgePlugin.LogWarning($"[DxvkDmaBuf] IDXGIVkInteropDevice1 unavailable hr=0x{hr:X8}");
                return false;
            }

            IntPtr* interopVtable = *(IntPtr**)_interop;
            _getVulkanHandles = GetDelegate<GetVulkanHandlesDelegate>(interopVtable[3]);
            _createTexture2DFromVkImage = GetDelegate<CreateTexture2DFromVkImageDelegate>(interopVtable[10]);
            _getVulkanHandles(_interop, out _vkInstance, out _vkPhysicalDevice, out _vkDevice);
            if (_vkInstance == IntPtr.Zero || _vkPhysicalDevice == IntPtr.Zero || _vkDevice == IntPtr.Zero)
            {
                SharedTextureBridgePlugin.LogWarning("[DxvkDmaBuf] DXVK returned empty Vulkan handles");
                return false;
            }

            _vulkanModule = LoadLibraryA("vulkan-1.dll");
            if (_vulkanModule == IntPtr.Zero)
            {
                SharedTextureBridgePlugin.LogWarning($"[DxvkDmaBuf] LoadLibrary(vulkan-1.dll) failed err=0x{Marshal.GetLastWin32Error():X8}");
                return false;
            }

            _vkGetInstanceProcAddr = GetProcDelegate<VkGetInstanceProcAddrDelegate>("vkGetInstanceProcAddr");
            _vkGetDeviceProcAddr = GetProcDelegate<VkGetDeviceProcAddrDelegate>("vkGetDeviceProcAddr");
            if (_vkGetInstanceProcAddr == null || _vkGetDeviceProcAddr == null)
                return false;

            _vkGetPhysicalDeviceMemoryProperties = GetInstanceProc<VkGetPhysicalDeviceMemoryPropertiesDelegate>("vkGetPhysicalDeviceMemoryProperties");
            _vkCreateImage = GetDeviceProc<VkCreateImageDelegate>("vkCreateImage");
            _vkDestroyImage = GetDeviceProc<VkDestroyImageDelegate>("vkDestroyImage");
            _vkGetImageMemoryRequirements = GetDeviceProc<VkGetImageMemoryRequirementsDelegate>("vkGetImageMemoryRequirements");
            _vkAllocateMemory = GetDeviceProc<VkAllocateMemoryDelegate>("vkAllocateMemory");
            _vkFreeMemory = GetDeviceProc<VkFreeMemoryDelegate>("vkFreeMemory");
            _vkBindImageMemory = GetDeviceProc<VkBindImageMemoryDelegate>("vkBindImageMemory");
            _vkGetMemoryFdPropertiesKHR = GetDeviceProc<VkGetMemoryFdPropertiesKHRDelegate>("vkGetMemoryFdPropertiesKHR");

            if (_vkGetPhysicalDeviceMemoryProperties == null || _vkCreateImage == null || _vkDestroyImage == null ||
                _vkGetImageMemoryRequirements == null || _vkAllocateMemory == null || _vkFreeMemory == null ||
                _vkBindImageMemory == null || _vkGetMemoryFdPropertiesKHR == null)
            {
                SharedTextureBridgePlugin.LogWarning("[DxvkDmaBuf] Required Vulkan entry point missing");
                return false;
            }

            _initialized = true;
            SharedTextureBridgePlugin.LogInfo($"[DxvkDmaBuf] Ready PE import instance=0x{_vkInstance.ToInt64():X} phys=0x{_vkPhysicalDevice.ToInt64():X} device=0x{_vkDevice.ToInt64():X}");
            return true;
        }

        internal ulong[] QuerySupportedModifiers()
        {
            return new ulong[] { 216172782120099860UL, 0UL };
        }

        internal bool TryImport(DbLinuxFrame frame, out DmaBufImportedTexture imported)
        {
            imported = null;
            if (!EnsureInitialized()) return false;
            if (frame.Fd < 0 || frame.Width == 0 || frame.Height == 0 || frame.Stride <= 0 || frame.PlaneCount != 1)
                return false;

            int fd = frame.Fd;
            bool fdConsumed = false;
            IntPtr vkImage = IntPtr.Zero;
            IntPtr vkMemory = IntPtr.Zero;
            IntPtr d3dTexture = IntPtr.Zero;
            IntPtr srv = IntPtr.Zero;

            try
            {
                VkExternalMemoryImageCreateInfo externalImage = new VkExternalMemoryImageCreateInfo
                {
                    SType = VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO,
                    HandleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT
                };
                VkSubresourceLayout planeLayout = new VkSubresourceLayout
                {
                    Offset = frame.Offset,
                    Size = 0,
                    RowPitch = (ulong)frame.Stride
                };
                VkImageDrmFormatModifierExplicitCreateInfoEXT modifierInfo = new VkImageDrmFormatModifierExplicitCreateInfoEXT
                {
                    SType = VK_STRUCTURE_TYPE_IMAGE_DRM_FORMAT_MODIFIER_EXPLICIT_CREATE_INFO_EXT,
                    PNext = (IntPtr)(&externalImage),
                    DrmFormatModifier = frame.Modifier,
                    DrmFormatModifierPlaneCount = 1,
                    PPlaneLayouts = (IntPtr)(&planeLayout)
                };
                VkImageCreateInfo imageInfo = new VkImageCreateInfo
                {
                    SType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO,
                    PNext = frame.HasModifier != 0 ? (IntPtr)(&modifierInfo) : (IntPtr)(&externalImage),
                    ImageType = VK_IMAGE_TYPE_2D,
                    Format = VK_FORMAT_B8G8R8A8_UNORM,
                    Extent = new VkExtent3D { Width = frame.Width, Height = frame.Height, Depth = 1 },
                    MipLevels = 1,
                    ArrayLayers = 1,
                    Samples = VK_SAMPLE_COUNT_1_BIT,
                    Tiling = frame.HasModifier != 0 ? VK_IMAGE_TILING_DRM_FORMAT_MODIFIER_EXT : VK_IMAGE_TILING_OPTIMAL,
                    Usage = VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT,
                    SharingMode = VK_SHARING_MODE_EXCLUSIVE,
                    InitialLayout = VK_IMAGE_LAYOUT_UNDEFINED
                };

                int vk = _vkCreateImage(_vkDevice, &imageInfo, IntPtr.Zero, out vkImage);
                if (vk != 0 || vkImage == IntPtr.Zero)
                {
                    SharedTextureBridgePlugin.LogWarning($"[DxvkDmaBuf] vkCreateImage failed result=0x{vk:X8} {frame.Width}x{frame.Height} modifier=0x{frame.Modifier:X}");
                    return false;
                }

                _vkGetImageMemoryRequirements(_vkDevice, vkImage, out var requirements);
                VkMemoryFdPropertiesKHR fdProps = new VkMemoryFdPropertiesKHR { SType = VK_STRUCTURE_TYPE_MEMORY_FD_PROPERTIES_KHR };
                vk = _vkGetMemoryFdPropertiesKHR(_vkDevice, VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT, fd, &fdProps);
                if (vk != 0)
                {
                    SharedTextureBridgePlugin.LogWarning($"[DxvkDmaBuf] vkGetMemoryFdPropertiesKHR failed result=0x{vk:X8}");
                    return false;
                }

                _vkGetPhysicalDeviceMemoryProperties(_vkPhysicalDevice, out var memoryProperties);
                uint compatibleBits = requirements.MemoryTypeBits & fdProps.MemoryTypeBits;
                if (!FindMemoryType(memoryProperties, compatibleBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT, out uint memoryTypeIndex))
                {
                    SharedTextureBridgePlugin.LogWarning($"[DxvkDmaBuf] No compatible memory type bits=0x{compatibleBits:X8}");
                    return false;
                }

                VkImportMemoryFdInfoKHR importInfo = new VkImportMemoryFdInfoKHR
                {
                    SType = VK_STRUCTURE_TYPE_IMPORT_MEMORY_FD_INFO_KHR,
                    HandleType = VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT,
                    Fd = fd
                };
                VkMemoryDedicatedAllocateInfo dedicatedInfo = new VkMemoryDedicatedAllocateInfo
                {
                    SType = VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO,
                    PNext = (IntPtr)(&importInfo),
                    Image = vkImage
                };
                VkMemoryAllocateInfo allocateInfo = new VkMemoryAllocateInfo
                {
                    SType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
                    PNext = (IntPtr)(&dedicatedInfo),
                    AllocationSize = requirements.Size,
                    MemoryTypeIndex = memoryTypeIndex
                };
                vk = _vkAllocateMemory(_vkDevice, &allocateInfo, IntPtr.Zero, out vkMemory);
                if (vk != 0 || vkMemory == IntPtr.Zero)
                {
                    SharedTextureBridgePlugin.LogWarning($"[DxvkDmaBuf] vkAllocateMemory import failed result=0x{vk:X8}");
                    return false;
                }
                fdConsumed = true;
                fd = -1;

                vk = _vkBindImageMemory(_vkDevice, vkImage, vkMemory, 0);
                if (vk != 0)
                {
                    SharedTextureBridgePlugin.LogWarning($"[DxvkDmaBuf] vkBindImageMemory failed result=0x{vk:X8}");
                    return false;
                }

                uint dxgiFormat = GetDxgiFormat(frame.Fourcc);
                D3D11_TEXTURE2D_DESC1 desc = new D3D11_TEXTURE2D_DESC1
                {
                    Width = frame.Width,
                    Height = frame.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = dxgiFormat,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                    BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET
                };
                hrCheck(_createTexture2DFromVkImage(_interop, &desc, vkImage, out d3dTexture), "CreateTexture2DFromVkImage");
                CreateShaderResourceView(d3dTexture, dxgiFormat, out srv);

                Texture2D unityTexture = Texture2D.CreateExternalTexture(
                    checked((int)frame.Width),
                    checked((int)frame.Height),
                    TextureFormat.BGRA32,
                    false,
                    false,
                    srv);
                unityTexture.name = $"DesktopBuddy Linux DMA-BUF {frame.Width}x{frame.Height}";
                unityTexture.wrapMode = TextureWrapMode.Clamp;

                imported = new DmaBufImportedTexture(this, unityTexture, d3dTexture, srv, vkImage, vkMemory, checked((int)frame.Width), checked((int)frame.Height), dxgiFormat);
                d3dTexture = IntPtr.Zero;
                srv = IntPtr.Zero;
                vkImage = IntPtr.Zero;
                vkMemory = IntPtr.Zero;
                return true;
            }
            catch (Exception ex)
            {
                SharedTextureBridgePlugin.LogWarning($"[DxvkDmaBuf] Import failed: {ex.Message}");
                return false;
            }
            finally
            {
                if (!fdConsumed && fd >= 0)
                    _close(fd);
                if (srv != IntPtr.Zero) Marshal.Release(srv);
                if (d3dTexture != IntPtr.Zero) Marshal.Release(d3dTexture);
                if (vkImage != IntPtr.Zero) _vkDestroyImage?.Invoke(_vkDevice, vkImage, IntPtr.Zero);
                if (vkMemory != IntPtr.Zero) _vkFreeMemory?.Invoke(_vkDevice, vkMemory, IntPtr.Zero);
            }
        }

        internal void ReleaseImported(DmaBufImportedTexture imported)
        {
            if (imported == null) return;
            if (imported.OwnsUnityTexture && imported.UnityTexture != null)
                UnityEngine.Object.Destroy(imported.UnityTexture);
            if (imported.ShaderResourceView != IntPtr.Zero)
                Marshal.Release(imported.ShaderResourceView);
            if (imported.D3dTexture != IntPtr.Zero)
                Marshal.Release(imported.D3dTexture);
            if (imported.VkImage != IntPtr.Zero)
                _vkDestroyImage?.Invoke(_vkDevice, imported.VkImage, IntPtr.Zero);
            if (imported.VkMemory != IntPtr.Zero)
                _vkFreeMemory?.Invoke(_vkDevice, imported.VkMemory, IntPtr.Zero);
        }

        private static uint GetDxgiFormat(uint drmFourcc)
        {
            return drmFourcc == DRM_FORMAT_XRGB8888
                ? DXGI_FORMAT_B8G8R8X8_UNORM
                : DXGI_FORMAT_B8G8R8A8_UNORM;
        }

        private void CreateShaderResourceView(IntPtr texture, uint dxgiFormat, out IntPtr srv)
        {
            var desc = new D3D11_SHADER_RESOURCE_VIEW_DESC
            {
                Format = dxgiFormat,
                ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D,
                Texture2D = new D3D11_TEX2D_SRV { MipLevels = 1 }
            };
            IntPtr* vtable = *(IntPtr**)UnityD3D11Device.D3dDevice;
            var createSrv = GetDelegate<CreateShaderResourceViewDelegate>(vtable[ID3D11Device_CreateShaderResourceView]);
            hrCheck(createSrv(UnityD3D11Device.D3dDevice, texture, &desc, out srv), "CreateShaderResourceView");
        }

        private static void hrCheck(int hr, string label)
        {
            if (hr < 0)
                throw new InvalidOperationException($"{label} failed hr=0x{hr:X8}");
        }

        private static bool FindMemoryType(VkPhysicalDeviceMemoryProperties props, uint bits, uint requiredFlags, out uint index)
        {
            for (uint i = 0; i < props.MemoryTypeCount; i++)
            {
                if ((bits & (1u << (int)i)) == 0)
                    continue;
                if ((props.MemoryTypes[(int)i].PropertyFlags & requiredFlags) == requiredFlags)
                {
                    index = i;
                    return true;
                }
            }
            index = 0;
            return false;
        }

        private T GetProcDelegate<T>(string name) where T : class
        {
            IntPtr proc = GetProcAddress(_vulkanModule, name);
            if (proc == IntPtr.Zero)
            {
                SharedTextureBridgePlugin.LogWarning($"[DxvkDmaBuf] Missing {name}");
                return null;
            }
            return GetDelegate<T>(proc);
        }

        private T GetInstanceProc<T>(string name) where T : class
        {
            IntPtr proc = _vkGetInstanceProcAddr(_vkInstance, name);
            if (proc == IntPtr.Zero)
                SharedTextureBridgePlugin.LogWarning($"[DxvkDmaBuf] Missing {name}");
            return proc == IntPtr.Zero ? null : GetDelegate<T>(proc);
        }

        private T GetDeviceProc<T>(string name) where T : class
        {
            IntPtr proc = _vkGetDeviceProcAddr(_vkDevice, name);
            if (proc == IntPtr.Zero)
                SharedTextureBridgePlugin.LogWarning($"[DxvkDmaBuf] Missing {name}");
            return proc == IntPtr.Zero ? null : GetDelegate<T>(proc);
        }

        private static T GetDelegate<T>(IntPtr ptr) where T : class
        {
            return Marshal.GetDelegateForFunctionPointer(ptr, typeof(T)) as T;
        }

        public void Dispose()
        {
            if (_interop != IntPtr.Zero)
            {
                Marshal.Release(_interop);
                _interop = IntPtr.Zero;
            }
            if (_vulkanModule != IntPtr.Zero)
            {
                FreeLibrary(_vulkanModule);
                _vulkanModule = IntPtr.Zero;
            }
            _initialized = false;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr LoadLibraryA(string fileName);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procName);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);
        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int _close(int fd);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void GetVulkanHandlesDelegate(IntPtr self, out IntPtr instance, out IntPtr physicalDevice, out IntPtr device);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateTexture2DFromVkImageDelegate(IntPtr self, D3D11_TEXTURE2D_DESC1* desc, IntPtr vkImage, out IntPtr texture2D);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr VkGetInstanceProcAddrDelegate(IntPtr instance, [MarshalAs(UnmanagedType.LPStr)] string name);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr VkGetDeviceProcAddrDelegate(IntPtr device, [MarshalAs(UnmanagedType.LPStr)] string name);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void VkGetPhysicalDeviceMemoryPropertiesDelegate(IntPtr physicalDevice, out VkPhysicalDeviceMemoryProperties properties);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int VkCreateImageDelegate(IntPtr device, VkImageCreateInfo* createInfo, IntPtr allocator, out IntPtr image);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void VkDestroyImageDelegate(IntPtr device, IntPtr image, IntPtr allocator);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void VkGetImageMemoryRequirementsDelegate(IntPtr device, IntPtr image, out VkMemoryRequirements requirements);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int VkAllocateMemoryDelegate(IntPtr device, VkMemoryAllocateInfo* allocateInfo, IntPtr allocator, out IntPtr memory);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void VkFreeMemoryDelegate(IntPtr device, IntPtr memory, IntPtr allocator);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int VkBindImageMemoryDelegate(IntPtr device, IntPtr image, IntPtr memory, ulong offset);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int VkGetMemoryFdPropertiesKHRDelegate(IntPtr device, uint handleType, int fd, VkMemoryFdPropertiesKHR* properties);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateShaderResourceViewDelegate(IntPtr device, IntPtr resource, D3D11_SHADER_RESOURCE_VIEW_DESC* desc, out IntPtr srv);

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_SAMPLE_DESC { public uint Count; public uint Quality; }
        private struct D3D11_TEXTURE2D_DESC1
        {
            public uint Width, Height, MipLevels, ArraySize, Format;
            public DXGI_SAMPLE_DESC SampleDesc;
            public uint Usage, BindFlags, CPUAccessFlags, MiscFlags, TextureLayout;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_TEX2D_SRV { public uint MostDetailedMip; public uint MipLevels; }
        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_SHADER_RESOURCE_VIEW_DESC
        {
            public uint Format, ViewDimension;
            public D3D11_TEX2D_SRV Texture2D;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct VkExtent3D { public uint Width, Height, Depth; }
        [StructLayout(LayoutKind.Sequential)]
        private struct VkExternalMemoryImageCreateInfo { public int SType; public IntPtr PNext; public uint HandleTypes; }
        [StructLayout(LayoutKind.Sequential)]
        private struct VkImageCreateInfo
        {
            public int SType; public IntPtr PNext; public uint Flags; public int ImageType; public uint Format;
            public VkExtent3D Extent; public uint MipLevels, ArrayLayers; public int Samples, Tiling;
            public uint Usage; public int SharingMode; public uint QueueFamilyIndexCount; public IntPtr PQueueFamilyIndices; public int InitialLayout;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct VkSubresourceLayout { public ulong Offset, Size, RowPitch, ArrayPitch, DepthPitch; }
        [StructLayout(LayoutKind.Sequential)]
        private struct VkImageDrmFormatModifierExplicitCreateInfoEXT
        {
            public int SType; public IntPtr PNext; public ulong DrmFormatModifier; public uint DrmFormatModifierPlaneCount; public IntPtr PPlaneLayouts;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct VkMemoryRequirements { public ulong Size, Alignment; public uint MemoryTypeBits; }
        [StructLayout(LayoutKind.Sequential)]
        private struct VkMemoryType { public uint PropertyFlags; public uint HeapIndex; }
        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct VkPhysicalDeviceMemoryProperties
        {
            public uint MemoryTypeCount;
            public fixed uint MemoryTypesRaw[64];
            public uint MemoryHeapCount;
            public fixed ulong MemoryHeapsRaw[32];
            public VkMemoryType[] MemoryTypes
            {
                get
                {
                    var values = new VkMemoryType[32];
                    fixed (uint* raw = MemoryTypesRaw)
                    {
                        for (int i = 0; i < values.Length; i++)
                        {
                            values[i].PropertyFlags = raw[i * 2];
                            values[i].HeapIndex = raw[i * 2 + 1];
                        }
                    }
                    return values;
                }
            }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct VkImportMemoryFdInfoKHR { public int SType; public IntPtr PNext; public uint HandleType; public int Fd; }
        [StructLayout(LayoutKind.Sequential)]
        private struct VkMemoryDedicatedAllocateInfo { public int SType; public IntPtr PNext; public IntPtr Image; public IntPtr Buffer; }
        [StructLayout(LayoutKind.Sequential)]
        private struct VkMemoryAllocateInfo { public int SType; public IntPtr PNext; public ulong AllocationSize; public uint MemoryTypeIndex; }
        [StructLayout(LayoutKind.Sequential)]
        private struct VkMemoryFdPropertiesKHR { public int SType; public IntPtr PNext; public uint MemoryTypeBits; }
    }

    internal sealed class DmaBufImportedTexture
    {
        private readonly DxvkDmaBufImporter _owner;
        internal Texture2D UnityTexture;
        internal readonly IntPtr D3dTexture;
        internal readonly IntPtr ShaderResourceView;
        internal readonly IntPtr VkImage;
        internal readonly IntPtr VkMemory;
        internal readonly int Width;
        internal readonly int Height;
        internal readonly uint DxgiFormat;
        internal bool OwnsUnityTexture;

        internal DmaBufImportedTexture(DxvkDmaBufImporter owner, Texture2D unityTexture, IntPtr d3dTexture, IntPtr srv, IntPtr vkImage, IntPtr vkMemory, int width, int height, uint dxgiFormat)
        {
            _owner = owner;
            UnityTexture = unityTexture;
            D3dTexture = d3dTexture;
            ShaderResourceView = srv;
            VkImage = vkImage;
            VkMemory = vkMemory;
            Width = width;
            Height = height;
            DxgiFormat = dxgiFormat;
            OwnsUnityTexture = true;
        }

        internal void UseExistingUnityTexture(Texture2D unityTexture)
        {
            if (UnityTexture != null && UnityTexture != unityTexture)
                UnityEngine.Object.Destroy(UnityTexture);
            UnityTexture = unityTexture;
            OwnsUnityTexture = false;
        }

        internal void Dispose()
        {
            _owner.ReleaseImported(this);
        }
    }
}
