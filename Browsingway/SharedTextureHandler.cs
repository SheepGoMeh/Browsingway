using Browsingway.Common;
using Dalamud.Bindings.ImGui;
using ImGuiScene;
using System.Numerics;
using D3D = SharpDX.Direct3D;
using D3D11 = SharpDX.Direct3D11;

namespace Browsingway;

internal class SharedTextureHandler : IDisposable
{
	private readonly D3D11.ShaderResourceView _view;
 	private readonly Vector2 _size;

	public SharedTextureHandler(IntPtr handle)
	{
		D3D11.Texture2D? textureSource = DxHandler.Device?.OpenSharedResource<D3D11.Texture2D>(handle);
		if (textureSource is null)
		{
			throw new Exception("Could not initialize shared texture");
		}

		_view = new(DxHandler.Device, textureSource, new D3D11.ShaderResourceViewDescription { Format = textureSource.Description.Format, Dimension = D3D.ShaderResourceViewDimension.Texture2D, Texture2D = { MipLevels = textureSource.Description.MipLevels } });
  		_size = new(textureSource.Description.Width, textureSource.Description.Height);
	}

	public void Dispose()
	{
		_view.Dispose();
	}

	public void Render()
	{
		ImGui.Image(new ImTextureID(_view), _size);
	}
}
