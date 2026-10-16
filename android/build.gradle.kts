// 顶层构建文件：只声明插件版本，不在此处直接应用（由各模块 apply）。
plugins {
    alias(libs.plugins.android.application) apply false
    alias(libs.plugins.kotlin.android) apply false
    alias(libs.plugins.kotlin.compose) apply false
}
