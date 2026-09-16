// AGP 9 compiles Kotlin itself; the Compose compiler plugin also pins the
// Kotlin version used for the whole build.
plugins {
    alias(libs.plugins.android.application) apply false
    alias(libs.plugins.kotlin.compose) apply false
}
