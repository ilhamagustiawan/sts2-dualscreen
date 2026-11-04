// sts2-dualscreen app: the launcher (Steam sign-in, download, setup), the
// Godot activity that runs the game, and the second-screen layer.
//
// The engine itself is not built here. tools/runtime/make-runtime.sh puts the
// custom Godot 4.5.1 Mono runtime into libs/ and src/main/{jniLibs,assets}.

import java.io.File

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

val appVersion = File(rootDir.parentFile, "VERSION").readText().trim()

android {
    namespace = "dev.sts2ds"
    compileSdk = 35
    buildToolsVersion = "35.0.0"
    ndkVersion = "28.1.13356709"

    defaultConfig {
        applicationId = "dev.sts2ds"
        minSdk = 26
        targetSdk = 35
        versionName = appVersion
        versionCode = appVersion.split('.').fold(0) { acc, part -> acc * 100 + part.toInt() }
        ndk { abiFilters += "arm64-v8a" }
    }

    externalNativeBuild {
        cmake {
            path = file("src/main/cpp/CMakeLists.txt")
            version = "3.22.1"
        }
    }

    buildTypes {
        getByName("debug") {
            // The runtime only has release engine binaries.
            isJniDebuggable = false
        }
        // For playing: not debuggable, so ART compiles it fully. No R8: Godot
        // and JavaSteam look classes up by name. Signed with the debug key;
        // sign with your own key to distribute.
        getByName("release") {
            isDebuggable = false
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("debug")
        }
        // For development: the release build, debuggable, so `adb shell
        // run-as dev.sts2ds` can pull logs and saves (`make dev-apk`).
        create("devel") {
            initWith(getByName("release"))
            isDebuggable = true
            matchingFallbacks += "release"
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    buildFeatures { buildConfig = true }

    packaging {
        jniLibs {
            // Mono and the GDExtensions are dlopen()ed by path, so they must
            // be extracted to the native library directory.
            useLegacyPackaging = true
            keepDebugSymbols += "**/*.so"
            pickFirsts += "lib/arm64-v8a/libc++_shared.so"
        }
        resources {
            excludes += listOf(
                "META-INF/LICENSE*", "META-INF/NOTICE*", "META-INF/DEPENDENCIES",
                "META-INF/INDEX.LIST", "META-INF/versions/**", "META-INF/*.kotlin_module",
                "META-INF/io.netty.versions.properties", "OSGI-INF/**",
            )
        }
    }

    androidResources {
        // Godot's own default; keeps .gitkeep etc. out and leaves _-prefixed dirs in.
        ignoreAssetsPattern = "!.svn:!.git:!.gitignore:!.ds_store:!*.scc:!CVS:!thumbs.db:!picasa.ini:!*~"
        noCompress += listOf("pck", "dll")
    }

    applicationVariants.all {
        outputs.all {
            (this as com.android.build.gradle.internal.api.BaseVariantOutputImpl).outputFileName =
                "sts2-dualscreen-$appVersion-${buildType.name}.apk"
        }
    }

    testOptions { unitTests.isReturnDefaultValues = true }
}

dependencies {
    // Godot 4.5.1 Java library with the custom engine inside, plus the FMOD
    // and .NET crypto Java classes the natives call into.
    implementation(fileTree("libs") { include("*.aar", "*.jar") })

    implementation("androidx.core:core:1.13.1")
    implementation("androidx.fragment:fragment:1.8.6")
    implementation("androidx.core:core-splashscreen:1.0.1")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.1")

    // Steam: sign-in, depot download and cloud saves (as in SilksongAndroid).
    implementation("in.dragonbra:javasteam:1.8.0")
    implementation("in.dragonbra:javasteam-depotdownloader:1.8.0")
    implementation("org.bouncycastle:bcprov-jdk18on:1.78.1")
    implementation("com.github.luben:zstd-jni:1.5.7-3@aar")
    implementation("org.tukaani:xz:1.9")
    implementation("com.google.protobuf:protobuf-java:4.29.2")
    implementation("com.squareup.okhttp3:okhttp:5.0.0-alpha.14")
    implementation("com.google.zxing:core:3.5.3")
    implementation("org.slf4j:slf4j-nop:2.0.17")

    testImplementation("junit:junit:4.13.2")
    // The real org.json, in place of android.jar's stubs, for GameSettingsFileTest.
    testImplementation("org.json:json:20240303")
}
