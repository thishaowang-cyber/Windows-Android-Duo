# Windows-Android-Duo

通过手机的传感器信息来同步电脑铰链的闭合角度，同时在 Android 和 Windows 上实现悬浮玻璃的效果。

具体说：把手机（或笔电屏盖）的**开合角度**变成一个可见的悬浮玻璃——角度驱动倾角，倾角驱动一块会磨砂、会折射、边缘有高光与雾环的玻璃。

同一套着色器数学跑在两端：

| 目录 | 是什么 | 技术栈 |
| --- | --- | --- |
| `android/` | 手机端 DUO app：读取铰链角度、本机渲染玻璃效果、并把角度通过局域网发给电脑 | Kotlin / Jetpack Compose + GLES 3.0 |
| `WindowsDuo/` | 电脑端桌面悬浮玻璃（macOS 原作的 Windows 复刻），以手机为角度源 | C# / .NET 8 + Vortice D3D11 + WinForms |
| `glass-shader-verify/` | 着色器逐像素数值验证工具（对照独立参考实现） | 原生 WebGL + HTML，无依赖 |

## 工作原理

**角度源**：手机用 `Sensor.TYPE_GRAVITY` 算屏盖倾角 `h = acos(−a_z/|a|)`（0=合盖，90=竖直，180=平放）。只用屏幕法线，所以手机在屏盖上平面旋转不改变读数，无需标定、无漂移。

**两端通信**：手机开 **TCP 服务端（端口 45680）**，电脑作为客户端连进去。方向是刻意的——Windows 防火墙默认拦入站、放出站，所以这个方向**不需要任何防火墙规则**。手机开启共享后屏幕显示 `IP:45680`，在电脑端填一次即可记住。

**玻璃几何**：保铰链的横向梯形（keystone）。竖直方向 1:1（上下边贴住屏幕），水平方向 `w = uEye / (uEye + dNorm·sin θ)`——铰链边保持原宽，远端收窄，磨砂与变暗随收窄递增。因为 `w ≤ 1` 恒成立，画面永不会溢出屏幕。

`uEye` 是**推导量而非可调参数**：等于「观看距离 ÷ 屏幕物理高度」。手机端从 `dumpsys display` 的真实 ydpi 反推屏幕物理高度；电脑端用 `GetDeviceCaps(VERTSIZE)` 拿面板毫米数。

## 预构建产物（`release/`）

| 文件 | 是什么 | 怎么用 |
| --- | --- | --- |
| `DUO-android-debug.apk` | 手机端，debug 签名（Android 12+ 真机验证过） | 传到手机直接安装；系统提示"未知来源"时允许即可 |
| `WindowsDuo-win-x64.zip` | 电脑端整套运行文件（exe + 依赖 + 着色器），自包含、免安装 | 解压后运行 `WindowsDuo\WindowsDuo.exe`，需要机器装有 **.NET 8 Desktop Runtime** |

两个产物都由本仓库这份源码于 2026-10-16 构建。

**Windows 端首次启动**：exe 未做代码签名。普通机器会弹 SmartScreen 蓝屏警告，点 **"更多信息 → 仍要运行"** 即可，之后不再提示；功能（含 GPU 截图与渲染）不受签名影响。**例外**：开着 Smart App Control 的机器（Win11 24H2 干净安装的默认状态）会直接拦死且无法绕过——那不是本项目的问题，是系统策略，详见构建一节自行编译（本地构建的 exe 无下载标记，不受此限）。

## 构建

### 手机端

```bash
cd android
./gradlew :app:assembleDebug      # 产物在 app/build/outputs/apk/debug/
./gradlew :app:testDebugUnitTest   # assembleDebug 不编译测试源码，需单独跑
```

需要 JDK 21 + Android SDK（AGP 8.13.2 / Kotlin 2.2.20 / Gradle 8.14.4）。`buildToolsVersion` 固定在 `app/build.gradle.kts` 里。

> 只有 debug 签名，没有 release 签名配置，所以这是开发/自用构建，不是可分发的发布包。

### 电脑端

```bash
cd WindowsDuo
dotnet build -c Release
```

需要 .NET 8 SDK + WindowsDesktop 运行时，NuGet 会拉 Vortice.Windows 3.8.3。

**验证（不启动窗口，无头跑）**：

```bash
dotnet bin/Release/net8.0-windows10.0.22621.0/WindowsDuo.dll --verify
```

做两件事：① 几何探针——像素着色器输出投影点，纯 C# oracle 重算同一公式，逐像素比对（当前最大差异 ~5e-7，浮点级）；② 在 0/20/40/60/80° 渲染成 PNG 输出到 `verify-out/`。

### 着色器验证工具

```bash
cd glass-shader-verify
python3 serve.py      # 或 node serve.mjs
```

浏览器打开后逐像素对照参考实现。