# Third-party dependencies

The project restores packages from public NuGet only. Package licenses and notices remain applicable to their own code.

| Dependency | License | Use |
| --- | --- | --- |
| [NLayer 1.16.0](https://github.com/naudio/NLayer) | MIT; Mark Heath and Andrew Ward | Fully managed MPEG audio decoding |
| [Microsoft.Extensions.* 10.0.0](https://github.com/dotnet/runtime) | MIT | Hosting, logging, options, DI and configuration |
| .NET / ASP.NET Core shared framework | MIT and bundled third-party notices | Managed runtime and HTTP server |

No native codec package, FFmpeg executable, ZLMediaKit binary or private Qcxt package is required. The .NET runtime itself uses platform facilities in the normal way; “pure .NET” refers to this project's implementation and dependencies, not to a replacement of operating-system networking or the runtime.
