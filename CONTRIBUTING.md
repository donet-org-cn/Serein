# Contributing

Use .NET 10 and run the commands in README.md or `pwsh -File tools/verify.ps1` before submitting changes. Tests use local loopback peers and synthetic media; no camera, native codec or private NuGet feed is required.

Keep the runtime and codec paths managed. New dependencies must be available publicly and include their license in THIRD-PARTY-NOTICES.md. Do not add FFmpeg process wrappers or native libraries to the core path.

When changing network parsers, include malformed/truncated input coverage. When changing media delivery, verify cancellation, frame ownership, queue pressure and disconnect/reconnect behavior. Avoid claiming throughput or latency without a reproducible benchmark and environment description.

Submit concise changes describing observable behavior, configuration impact and validation. Keep real device addresses, credentials, certificates and deployment logs out of the repository.
