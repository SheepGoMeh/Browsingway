using Dalamud.Bindings.ImGui;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace Browsingway;

internal unsafe class SharedTextureHandler : IDisposable
{
	private readonly ID3D11ShaderResourceView* _view;
	private ID3D11Texture2D* _texture;
	private readonly Vector2 _size;
	private readonly ImTextureID _textureId;

	private readonly MemoryMappedFile? _mmf;
	private readonly MemoryMappedFile? _mmfIdx;
	private readonly int _cpuWidth;
	private readonly int _cpuHeight;
	private int _dirtyX;
	private int _dirtyY;
	private int _dirtyWidth;
	private int _dirtyHeight;

	public SharedTextureHandler(IntPtr handle, Guid overlayGuid, int width, int height, int dirtyX, int dirtyY, int dirtyWidth, int dirtyHeight)
	{
		ID3D11Device* device = DxHandler.Device;
		if (device == null)
		{
			throw new Exception("Device is null");
		}

		_cpuWidth = width;
		_cpuHeight = height;
		_dirtyX = dirtyX;
		_dirtyY = dirtyY;
		_dirtyWidth = dirtyWidth > 0 ? dirtyWidth : width;
		_dirtyHeight = dirtyHeight > 0 ? dirtyHeight : height;

		if (handle != IntPtr.Zero)
		{
			// Attempt GPU shared resource path
			Guid texture2DGuid = typeof(ID3D11Texture2D).GUID;
			void* texturePtr;
			HRESULT hr = device->OpenSharedResource((HANDLE)handle, &texture2DGuid, &texturePtr);
			if (hr.SUCCEEDED)
			{
				_texture = (ID3D11Texture2D*)texturePtr;

				D3D11_TEXTURE2D_DESC texDesc;
				_texture->GetDesc(&texDesc);

				D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc = new()
				{
					Format = texDesc.Format,
					ViewDimension = D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_TEXTURE2D,
					Texture2D = new D3D11_TEX2D_SRV { MostDetailedMip = 0, MipLevels = texDesc.MipLevels }
				};

				ID3D11ShaderResourceView* view;
				hr = device->CreateShaderResourceView((ID3D11Resource*)_texture, &srvDesc, &view);
				if (hr.FAILED)
				{
					_texture->Release();
					throw new Exception($"Could not create shader resource view: {hr}");
				}

				_view = view;
				_size = new Vector2(texDesc.Width, texDesc.Height);
				_textureId = new ImTextureID((nint)_view);
				return;
			}
		}

		// CPU fallback: read pixels from named shared memory and upload via UpdateSubresource
		if (width <= 0 || height <= 0)
		{
			throw new Exception("CPU fallback requires valid texture dimensions.");
		}

		string mmfName = $"BrowsingwayCpuFrame_{overlayGuid:N}";
		_mmf = MemoryMappedFile.OpenExisting(mmfName, MemoryMappedFileRights.Read);
		_mmfIdx = MemoryMappedFile.OpenExisting(mmfName + "_idx", MemoryMappedFileRights.Read);
		_texture = CreateCpuUploadTexture(device, width, height);
		UploadCpuFrame(device);

		D3D11_TEXTURE2D_DESC cpuTexDesc;
		_texture->GetDesc(&cpuTexDesc);

		D3D11_SHADER_RESOURCE_VIEW_DESC cpuSrvDesc = new()
		{
			Format = cpuTexDesc.Format,
			ViewDimension = D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_TEXTURE2D,
			Texture2D = new D3D11_TEX2D_SRV { MostDetailedMip = 0, MipLevels = cpuTexDesc.MipLevels }
		};

		ID3D11ShaderResourceView* cpuView;
		HRESULT cpuHr = device->CreateShaderResourceView((ID3D11Resource*)_texture, &cpuSrvDesc, &cpuView);
		if (cpuHr.FAILED)
		{
			_texture->Release();
			throw new Exception($"Could not create CPU fallback shader resource view: {cpuHr}");
		}

		_view = cpuView;
		_size = new Vector2(width, height);
		_textureId = new ImTextureID((nint)_view);
	}

	private static ID3D11Texture2D* CreateCpuUploadTexture(ID3D11Device* device, int width, int height)
	{
		D3D11_TEXTURE2D_DESC desc = new()
		{
			Width = (uint)width,
			Height = (uint)height,
			MipLevels = 1,
			ArraySize = 1,
			Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
			SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
			Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
			BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
			CPUAccessFlags = 0,
			MiscFlags = 0
		};

		ID3D11Texture2D* texture;
		HRESULT hr = device->CreateTexture2D(&desc, null, &texture);
		if (hr.FAILED)
		{
			throw new Exception($"Could not create CPU fallback texture: {hr}");
		}

		return texture;
	}

	private void UploadCpuFrame(ID3D11Device* device)
	{
		int rowPitch = _cpuWidth * 4;
		int frameSize = rowPitch * _cpuHeight;

		// Read which slot was last fully written by the renderer
		int slot;
		using (MemoryMappedViewAccessor idxAccessor = _mmfIdx!.CreateViewAccessor(0, 4, MemoryMappedFileAccess.Read))
		{
			slot = idxAccessor.ReadInt32(0);
		}

		long slotOffset = (long)(slot & 1) * frameSize;
		using MemoryMappedViewStream stream = _mmf!.CreateViewStream(slotOffset, frameSize, MemoryMappedFileAccess.Read);
		byte[] buffer = new byte[frameSize];
		stream.ReadExactly(buffer);

		D3D11_BOX dirtyBox = new()
		{
			left = (uint)Math.Max(0, _dirtyX),
			top = (uint)Math.Max(0, _dirtyY),
			right = (uint)Math.Min(_cpuWidth, _dirtyX + _dirtyWidth),
			bottom = (uint)Math.Min(_cpuHeight, _dirtyY + _dirtyHeight),
			front = 0,
			back = 1
		};

		ID3D11DeviceContext* context;
		device->GetImmediateContext(&context);

		fixed (byte* pBuffer = buffer)
		{
			IntPtr srcPtr = (IntPtr)pBuffer + (_dirtyY * rowPitch) + (_dirtyX * 4);
			context->UpdateSubresource(
				(ID3D11Resource*)_texture,
				0,
				&dirtyBox,
				srcPtr.ToPointer(),
				(uint)rowPitch,
				(uint)frameSize);
		}

		context->Release();
	}

	public void UpdateDirtyRect(int dirtyX, int dirtyY, int dirtyWidth, int dirtyHeight)
	{
		_dirtyX = dirtyX;
		_dirtyY = dirtyY;
		_dirtyWidth = dirtyWidth > 0 ? dirtyWidth : _cpuWidth;
		_dirtyHeight = dirtyHeight > 0 ? dirtyHeight : _cpuHeight;
	}

	public void Dispose()
	{
		_view->Release();
		_texture->Release();
		_mmf?.Dispose();
		_mmfIdx?.Dispose();
	}

	public void Render()
	{
		if (_mmf != null)
		{
			ID3D11Device* device = DxHandler.Device;
			if (device != null)
			{
				UploadCpuFrame(device);
			}
		}

		ImGui.Image(_textureId, _size);
	}
}