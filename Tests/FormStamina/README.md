Runs production stamina-policy and HUD code against state doubles, checks compiled mod integration, and interprets the installed game's unlimited-stamina early-return paths. Also installs the actual Harmony patch class on the state doubles to verify both hooks and exit behavior. It does not simulate Unity rendering, physics or multiplayer.

The project targets .NET Framework 4.7.2 to match the mod/game Harmony assembly; that assembly's runtime initialization is incompatible with .NET 8.

```powershell
dotnet build Transform.csproj -c Release -v minimal
dotnet run --project Tests/FormStamina/FormStamina.csproj -c Release -- 'bin/Release/Thanks.Transform.dll' 'D:\Steam\steamapps\common\PEAK\PEAK_Data\Managed'
```

Coverage includes zero/low stored stamina, consumption/exhaustion early returns, full climbing strength, expiry of a native temporary buff, source-player isolation, pooling/exit, HUD reactivation, reversible optional-bar hiding, free-camera HUD ownership, missing canvas fallback, and deletion of old stamina-sharing/cost/bar branches.
