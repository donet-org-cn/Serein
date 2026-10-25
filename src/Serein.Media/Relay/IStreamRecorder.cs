namespace Serein.Media.Relay;

/// <summary>
/// 流录制器接口。解耦 MediaStreamHub 与具体录制实现。
/// </summary>
public interface IStreamRecorder : IDisposable
{
    /// <summary>收到媒体帧时调用。</summary>
    /// <param name="frame">媒体帧。</param>
    void OnFrame(PooledMediaFrame frame);

    /// <summary>停止录制并返回录制信息。</summary>
    /// <returns>录制文件路径、文件字节数与录制时长。</returns>
    (string FilePath, long FileSize, TimeSpan Duration) Stop();
}
