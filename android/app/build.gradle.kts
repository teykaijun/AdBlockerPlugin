import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
}

// Release signing is optional. Provide android/keystore.properties (see
// README) or the ADBLOCKER_KEYSTORE_* environment variables; otherwise the
// release build is signed with the debug key so it can still be sideloaded.
val keystoreProperties = Properties().apply {
    val file = rootProject.file("keystore.properties")
    if (file.exists()) file.inputStream().use { load(it) }
}

fun signingValue(key: String, env: String): String? =
    keystoreProperties.getProperty(key) ?: System.getenv(env)

android {
    namespace = "com.teykaijun.adblocker"
    compileSdk = 37

    defaultConfig {
        applicationId = "com.teykaijun.adblocker"
        minSdk = 26
        // Android 16 is the newest release whose behaviour changes this app was checked against.
        targetSdk = 36
        versionCode = 3
        versionName = "1.2.0"
    }

    // The built-in blocklists are shared with the Windows app and live in the
    // repository's filters/ folder; they are packaged as assets as-is.
    sourceSets {
        getByName("main") {
            assets.directories.add("../../filters")
        }
    }

    signingConfigs {
        val storePath = signingValue("storeFile", "ADBLOCKER_KEYSTORE_FILE")
        if (storePath != null) {
            create("release") {
                storeFile = rootProject.file(storePath)
                storePassword = signingValue("storePassword", "ADBLOCKER_KEYSTORE_PASSWORD")
                keyAlias = signingValue("keyAlias", "ADBLOCKER_KEY_ALIAS")
                keyPassword = signingValue("keyPassword", "ADBLOCKER_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = signingConfigs.findByName("release") ?: signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    packaging {
        resources.excludes += "/META-INF/{AL2.0,LGPL2.1}"
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.material.icons.core)
    implementation(libs.kotlinx.coroutines.android)
    debugImplementation(libs.androidx.compose.ui.tooling)

    testImplementation(libs.junit)
}
