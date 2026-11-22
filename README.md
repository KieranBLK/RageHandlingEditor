# RageHandlingEditor (RHE)
A fully-fledged handling editor for GTA 5 singleplayer, inspired by ikt's RealTimeHandlingEditor and built on ScriptHookVDotNetEnhanced and LemonUI, supporting both Legacy and Enhanced editions. Designed with user experience in mind, for a convenient and straightforward way to edit handlings to your liking. Built with love and passion

Mod page: https://www.gta5-mods.com/scripts/ragehandlingeditor-rhe-legacy-enhanced

## BUILDING FROM SOURCE
### The Script
- Needs the .NET SDK, which fetches the .NET Framework 4.8 reference assemblies on its own
- Reads ScriptHookVDotNet3.dll and LemonUI.SHVDN3.dll from `C:\Temp\`, or from another folder passed as `-p:ReferenceDir=<folder>\`
- `dotnet build RHE/RHE.csproj -c Release` builds `RageHandlingEditor.dll`, which goes in the game's `scripts` folder
- The source is written in C# 5, the newest version the .NET Framework's own compiler accepts

### The Plugins
- Need the Visual Studio 2026 Build Tools with the C++ workload, and the [ScriptHookV SDK](https://www.dev-c.com/gtav/scripthookv/)
- `msbuild RHE/Accessories/<Name>/<Name>.vcxproj -p:Configuration=Release -p:Platform=x64 -p:SdkDir=<SDK folder>\` builds `<Name>.asi` into that project's `bin` folder, which goes in the game root

## LICENSE
This mod is licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE). You may use, change, and share it for any noncommercial purpose. Redistribution of the mod, in any form, edited or not, must include this license, in full, as is.
