# Narrow Master Minded
This Legion TD2 mod was originally made for [Pennywise](https://www.twitch.tv/pennywiseuk), to only allow the mastermind legion Yolo (aka _the only good option_)\
But it was extended to make the option configurable directly in game with a new options tab.

Additionally, this mod will serve as demo/tutorial for the LTD2 modding framework once it is available.\
It is therefore copiously commented to help anyone wanting to understand its inner workings.

## Installation
- Close the game
- If not already done, follow this guide to install [BepInEx](https://github.com/LegionTD2-Mods/.github/wiki/Installation-of-BepInEx)
- Download the latest [release](https://github.com/LegionTD2-Mods/NarrowMasterMinded/releases/latest), and drop `NarrowMasterMinded.dll` inside your `Legion TD 2/BepInEx/plugins/` folder
- You are done, you can start the game and enjoy!

## Build
This mod is made using [BepInEx](https://github.com/BepInEx/BepInEx), which is required to build.\
Using JetBrain's Rider, you can use this as quick 'build and deploy' script:

```
dotnet build;
cp .\bin\Debug\netstandard2.0\NarrowMasterMinded.dll 'C:\SteamLibrary\steamapps\common\Legion TD 2\BepInEx\plugins\';
```
