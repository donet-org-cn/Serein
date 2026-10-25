# NuGet 发布流水线

工作流：[Publish NuGet](https://github.com/hnlyf168/Serein/actions/workflows/publish-nuget.yml)，文件为 `.github/workflows/publish-nuget.yml`。

## 执行方式

- 推送 `v*` 标签：从标签去掉 `v` 得到包版本；验证通过后上传到 nuget.org。
- 在 Actions 中选择 Run workflow：填写不带 `v` 的 `version`；留空时使用 `Directory.Build.props` 中的版本。默认 `publish=false`，只验证和打包；勾选后才上传。
- 手动上传只能从默认分支 `main` 或版本标签运行。普通源码 push 和 PR 只运行现有 CI，不上传 NuGet 包。

例如发布预览版：

```sh
git tag v0.1.0-preview.1
git push origin v0.1.0-preview.1
```

正式版使用 `v0.1.0`。标签版本会同时传给构建、打包和四个包之间的依赖，不必先改写默认开发版本。版本必须是 `MAJOR.MINOR.PATCH[-prerelease]`，不支持前导零或构建元数据后缀。

## 发布内容与验证

工作流先在 Windows、Linux 上构建、检查托管依赖、运行媒体与集成回归、验证包清单。两边成功后，发布步骤下载本次运行的 Linux 构建产物，按以下顺序上传：

1. `Serein.Media`
2. `Serein.Rtsp`
3. `Serein.Hosting`
4. `Serein.AspNetCore`

上传前再次检查包 ID、版本和内部依赖版本。产物保留 30 天。重复执行使用 `--skip-duplicate` 跳过已存在的版本，因此某个包失败后可以重跑；NuGet 不支持四个包的原子事务。

## 配置 NuGet 身份

两种方式任选其一。如果配置了 `NUGET_API_KEY`，流水线优先使用它。不要把任何 API Key 写入源码、配置文件或聊天消息。

### API Key

1. 登录 nuget.org，在 [API Keys](https://www.nuget.org/account/apikeys) 创建允许推送新包和新版本的 Key，包范围限制为 `Serein.*`。
2. 打开 [GitHub Actions Secrets](https://github.com/hnlyf168/Serein/settings/secrets/actions)，添加 Repository secret：`NUGET_API_KEY`，值为刚创建的 Key。
3. 手动运行流水线并勾选 `publish`，或推送版本标签。

Secret 也可以放在仓库的 `nuget` Environment 中。密钥过期时在 GitHub 更新即可，不需要修改工作流。

### OIDC Trusted Publishing

按 [NuGet 官方 Trusted Publishing 文档](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing) 在 nuget.org 配置信任策略：

| 字段 | 值 |
| --- | --- |
| Repository owner | `hnlyf168` |
| Repository | `Serein` |
| Workflow file | `publish-nuget.yml`，只填文件名 |
| Environment | `nuget` |
| 包范围 | `Serein.*`，允许新包与新版本 |

随后在 [GitHub Actions Variables](https://github.com/hnlyf168/Serein/settings/variables/actions) 添加 `NUGET_USER`，值为 NuGet 个人账户用户名，不是邮箱；它也不一定等于 GitHub 用户名。使用此方式时不配置 `NUGET_API_KEY`。

发布 job 使用 `NuGet/login@v1` 获取临时 API Key，且只有发布 job 具有 `id-token: write` 权限。仓库必须已在 NuGet 配置匹配的信任策略，只有 GitHub 变量仍不足以发布。

## 本地预演

```powershell
./tools/resolve-release.ps1 -Tag v0.1.0-preview.2
./tools/verify.ps1 -Version 0.1.0-preview.2
./tools/publish-nuget.ps1 -Version 0.1.0-preview.2 -DryRun
```

上述命令不会上传包。`verify.ps1` 每次打包前清理 `artifacts/packages` 中旧的 Serein 包，防止混入其他版本。若需要本地实际推送，可在进程环境中设置 `NUGET_API_KEY`，然后运行不带 `-DryRun` 的发布脚本。

NuGet 包名是否可用、账号是否有对应包的所有权以及 NuGet 的最终索引/验证结果，以 nuget.org 返回为准。创建流水线并不意味着已经发布第一个包。
