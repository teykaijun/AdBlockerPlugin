package com.teykaijun.adblocker.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

/** Top and bottom of the red shield gradient, shared with the extension icon. */
val ShieldTop = Color(0xFFF04A52)
val ShieldBottom = Color(0xFFB21E2B)

private val LightColors = lightColorScheme(
    primary = Color(0xFFB42330),
    onPrimary = Color(0xFFFFFFFF),
    primaryContainer = Color(0xFFFFDAD8),
    onPrimaryContainer = Color(0xFF410007),
    secondary = Color(0xFF775654),
    onSecondary = Color(0xFFFFFFFF),
    secondaryContainer = Color(0xFFFFDAD7),
    onSecondaryContainer = Color(0xFF2C1513),
    tertiary = Color(0xFF735B2E),
    onTertiary = Color(0xFFFFFFFF),
    tertiaryContainer = Color(0xFFFFDEA7),
    onTertiaryContainer = Color(0xFF281900),
    error = Color(0xFFBA1A1A),
    onError = Color(0xFFFFFFFF),
    errorContainer = Color(0xFFFFDAD6),
    onErrorContainer = Color(0xFF410002),
    background = Color(0xFFFFF8F7),
    onBackground = Color(0xFF231918),
    surface = Color(0xFFFFF8F7),
    onSurface = Color(0xFF231918),
    surfaceVariant = Color(0xFFF5DDDB),
    onSurfaceVariant = Color(0xFF534342),
    outline = Color(0xFF857371),
    outlineVariant = Color(0xFFD8C2BF),
    surfaceContainerLowest = Color(0xFFFFFFFF),
    surfaceContainerLow = Color(0xFFFFF0EF),
    surfaceContainer = Color(0xFFFCEAE8),
    surfaceContainerHigh = Color(0xFFF6E4E2),
    surfaceContainerHighest = Color(0xFFF0DEDD),
)

private val DarkColors = darkColorScheme(
    primary = Color(0xFFFFB3AE),
    onPrimary = Color(0xFF68000F),
    primaryContainer = Color(0xFF92001C),
    onPrimaryContainer = Color(0xFFFFDAD8),
    secondary = Color(0xFFE7BDB9),
    onSecondary = Color(0xFF442928),
    secondaryContainer = Color(0xFF5D3F3D),
    onSecondaryContainer = Color(0xFFFFDAD7),
    tertiary = Color(0xFFE3C28C),
    onTertiary = Color(0xFF402D04),
    tertiaryContainer = Color(0xFF594319),
    onTertiaryContainer = Color(0xFFFFDEA7),
    error = Color(0xFFFFB4AB),
    onError = Color(0xFF690005),
    errorContainer = Color(0xFF93000A),
    onErrorContainer = Color(0xFFFFDAD6),
    background = Color(0xFF1A1111),
    onBackground = Color(0xFFF1DEDC),
    surface = Color(0xFF1A1111),
    onSurface = Color(0xFFF1DEDC),
    surfaceVariant = Color(0xFF534342),
    onSurfaceVariant = Color(0xFFD8C2BF),
    outline = Color(0xFFA08C8A),
    outlineVariant = Color(0xFF534342),
    surfaceContainerLowest = Color(0xFF140C0C),
    surfaceContainerLow = Color(0xFF231919),
    surfaceContainer = Color(0xFF271D1D),
    surfaceContainerHigh = Color(0xFF322827),
    surfaceContainerHighest = Color(0xFF3D3231),
)

@Composable
fun AdBlockerTheme(darkTheme: Boolean = isSystemInDarkTheme(), content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = if (darkTheme) DarkColors else LightColors, content = content)
}
