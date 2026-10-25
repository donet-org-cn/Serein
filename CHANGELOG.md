# Changelog

## 0.1.0-preview.1

- Extracted the media-only engine into Serein.Media, Serein.Rtsp, Serein.Hosting and Serein.AspNetCore on .NET 10.
- Removed private foundation dependencies in favor of standard Microsoft hosting abstractions.
- Excluded SIP, device registration, device control and telephony services.
- Generalized PS/RTP input and audio processing names, preserving frame ownership, recovery, recording and lifecycle coverage.
- Added unified hosting, RTSP source reconnection, optional RTSP output, FLV endpoints and source status API.
- Added a standalone server example, public NuGet restore, package metadata, MIT license and CI.
- Made RTSP and TCP-FLV startup wait for a bound listener under .NET 10.
- Wait for listener rollback before surfacing startup failures from RTSP, TCP-FLV and PS/RTP receivers.
