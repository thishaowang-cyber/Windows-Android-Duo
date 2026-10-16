package com.example.laptophinge.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

/**
 * 主题。
 *
 * 恒用深色配色，不跟随系统：首页的底是 DUO 的色斑场、玻璃页的底是全屏画面，两者本身就暗，
 * 白字是唯一的可读组合；跟随系统到了浅色模式会变成深字压暗底。
 * 与电脑端 Windows Duo 同一套色：强调色 #8B7CF7，正文白，次要文字 196/198/214。
 */
private val DuoColors = darkColorScheme(
    primary = Color(0xFF8B7CF7),
    onPrimary = Color(0xFF191327),
    secondary = Color(0xFFB8C7DC),
    background = Color(0xFF140F20),
    onBackground = Color(0xFFEDEAF7),
    surface = Color(0xFF1D1A28),
    onSurface = Color(0xFFEDEAF7),
    surfaceVariant = Color(0xFF2A2637),
    onSurfaceVariant = Color(0xFFC4C6D6),
    error = Color(0xFFFF6B63),
)

@Composable
fun LaptopHingeTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = DuoColors, content = content)
}
