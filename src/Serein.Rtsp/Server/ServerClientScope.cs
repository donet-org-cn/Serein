namespace Serein.Rtsp.Server;

/// <summary>记录服务器回调的连接归属与有效期，避免停止时自等待。</summary>
internal sealed class ServerClientScope
{
    /// <summary>记录服务器回调的连接归属与有效期，避免停止时自等待。</summary>
    /// <param name="lifetime">宿主应用生命周期控制器。</param>
    internal ServerClientScope(CancellationTokenSource lifetime) => Lifetime = lifetime;
    /// <summary>该回调所属的生命周期标识。</summary>
    internal CancellationTokenSource Lifetime { get; }
    /// <summary>回调作用域是否仍在执行。</summary>
    internal bool Active = true;
}
