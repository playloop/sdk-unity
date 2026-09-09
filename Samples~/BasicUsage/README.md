# Basic Usage Sample

Drop `PlayloopBasicUsage.cs` onto any GameObject in a scene. Set the **API key** in the Inspector, hit Play, and watch the console for the response.

What it demonstrates:
- Constructing `PlayloopClient` with `PlayloopOptions`
- Reading a file with `File.ReadAllBytes` (works on Editor / Standalone / Mobile; for **WebGL** use `UnityWebRequest` to download bytes instead)
- Calling `Sessions.IngestAsync` and printing the returned insights
- Starting the telemetry auto-batch loop and tracking a few events
- Cleanly disposing the client in `OnDestroy`
