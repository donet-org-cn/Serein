namespace Serein.Media.Relay;

/// <summary>可选的录制生命周期信息；原有 IStreamRecorder 实现无需修改。</summary>
public interface IStreamRecorderLifecycle
{
    /// <summary>
    /// 实际创建文件时完成，结果为文件路径；未创建文件即停止时结果为 null。
    /// 实现应异步调度此任务的延续，避免调用方阻塞写盘线程。
    /// </summary>
    Task<string?> RecordingStarted { get; }
}
