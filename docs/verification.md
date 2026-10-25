# 验证记录

验证日期：2026-10-25（Asia/Shanghai）。

实际环境：Windows x64、.NET SDK 10.0.402、net10.0 Release。

| 检查 | 结果 |
| --- | --- |
| 全解决方案构建，警告视为错误 | 通过，0 警告、0 错误 |
| 媒体回归 | 44 项通过 |
| 托管与分发集成 | 4 项通过 |
| 托管依赖检查 | 通过：无私有基础包、原生依赖资产、P/Invoke 或外部进程入口 |
| NuGet 打包 | 4 个 0.1.0-preview.1 包 |
| 独立项目消费 NuGet 并启动 HTTP 服务 | 通过；通过本地包源还原，无源码项目引用 |

集成测试覆盖真实回环网络上的 RTSP 输入、HTTP-FLV、WebSocket-FLV、RTSP 输出和停机清理；另有 UDP PS/RTP 接入测试。音视频负载为合成测试数据，不需要外部摄像头。

Windows 和 Linux 均已在 GitHub Actions 上实际验证通过。`v0.1.0-preview.1` 的[正式发布运行](https://github.com/hnlyf168/Serein/actions/runs/40179376184)完成了两种系统的测试、四个包的验证、NuGet OIDC 登录与上传。

没有对生产设备、规模吞吐、连续长时间运行、端到端延迟或浏览器解码兼容性作实测结论。

可通过仓库根目录的 `tools/verify.ps1` 重现。此测试套件为可执行程序，不能用成功退出但未发现测试的 `dotnet test` 替代。
