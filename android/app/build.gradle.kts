plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
}

android {
    namespace = "com.example.laptophinge"
    compileSdk = 37

    /*
     * 显式钉住 build-tools 版本。
     *
     * 不写这一行时 AGP 会按自己的默认表去要某个特定 revision（本机上是 35.0.0），
     * 而本机 SDK 只装了 34.0.0 / 36.1.0 / 37.0.0，于是构建在「确定任务依赖」阶段就失败。
     * 钉住 36.1.0（已安装且与 compileSdk 37 兼容）可以让构建完全离线、可复现。
     */
    buildToolsVersion = "36.1.0"

    defaultConfig {
        applicationId = "com.example.laptophinge"
        // 24 覆盖约 98% 在网设备，且 BLE Peripheral（阶段五）在此版本可用
        minSdk = 24
        targetSdk = 36
        versionCode = 1
        versionName = "0.1.0"

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro",
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlin {
        compilerOptions {
            jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
        }
    }

    buildFeatures {
        compose = true
    }

    packaging {
        resources {
            excludes += "/META-INF/{AL2.0,LGPL2.1}"
        }
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.activity.compose)

    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.ui)
    implementation(libs.androidx.ui.graphics)
    implementation(libs.androidx.ui.tooling.preview)
    implementation(libs.androidx.material3)
    debugImplementation(libs.androidx.ui.tooling)

    // 纯 JVM 单元测试：核心数学（四元数 / 铰链轴 / 角度映射）不依赖 Android 框架
    testImplementation(libs.junit)
}
