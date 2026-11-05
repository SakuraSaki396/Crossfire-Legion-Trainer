# 第三方组件说明

本仓库自身的代码以 MIT 许可发布（见 `LICENSE`）。
但**编译出来的安装器 exe 内嵌了以下第三方运行时**，它们各有自己的许可，分发 exe 时需要保留相应声明。

| 组件 | 用途 | 许可 | 上游 |
|---|---|---|---|
| BepInEx 5.4.23.2 | Unity Mono 插件加载框架（`BepInEx\core`） | LGPL-2.1 | https://github.com/BepInEx/BepInEx |
| Doorstop（`winhttp.dll` + `doorstop_config.ini`） | BepInEx 的注入器 | LGPL-2.1 | https://github.com/NeighTools/UnityDoorstop |
| HarmonyX（`0Harmony.dll`） | 运行时方法补丁 | MIT | https://github.com/BepInEx/HarmonyX |
| MonoMod（`MonoMod.*.dll`） | HarmonyX 依赖的底层补丁库 | MIT | https://github.com/MonoMod/MonoMod |
| Mono.Cecil | 程序集读写 | MIT | https://github.com/jbevain/cecil |

LGPL 组件的源码可从上面链接获取；安装器只是把它们**原样复制**到游戏目录，没有修改其二进制。

`payload.zip` 与安装器 exe **不进入版本库**（见 `.gitignore`），需要时按 `README.md` 的「从源码构建」一节在本地生成。
如果你把 exe 挂到 GitHub Releases，建议同时附上本文件。

游戏本体、游戏资源（`Data\Attributes.zip` 中的属性定义等）版权归游戏开发商/发行商所有，本仓库不包含任何游戏文件。
