package com.example.laptophinge.ui

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.content.ContextCompat
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlin.math.max
import kotlin.math.min
import com.example.laptophinge.HingeUiState
import com.example.laptophinge.SensorStatusText
import com.example.laptophinge.glass.GlassSurfaceView
import com.example.laptophinge.ui.duo.Duo
import com.example.laptophinge.ui.duo.DuoCard
import com.example.laptophinge.ui.duo.DuoChip
import com.example.laptophinge.ui.duo.DuoPage
import com.example.laptophinge.ui.duo.DuoPillButton
import com.example.laptophinge.ui.duo.GlassSurface
import com.example.laptophinge.ui.duo.saveWallpaper

/**
 * 主界面：一屏之内把「角度、可信度、标定状态、原始数据」全摆出来。
 *
 * 设计原则：现场排障优先。项目最大的失败风险不是算法，而是
 * 「夹具没贴平 / 磁干扰 / 轴估计错了 / 系统把采样降频了」这类工程问题，
 * 因此所有中间量都必须可见，不能只显示一个漂亮的角度数字。
 *
 * 视觉走 DUO 玻璃语言，与电脑端 Windows Duo 首页同源（见 [com.example.laptophinge.ui.duo]）：
 * 一页铺底色斑场，读数装在 hero 玻璃块里，其余是卡片堆——比例常数都按面板宽度取，
 * 所以手机上和电脑端看起来是同一种东西。
 */
@Composable
fun AngleScreen(
    state: HingeUiState,
    sending: Boolean,
    shareAddress: String?,
    onToggleLogging: () -> Unit,
    onToggleSending: () -> Unit,
    onOpenGlass: () -> Unit,
) {
    val scroll = rememberScrollState()
    val context = LocalContext.current
    val prefs = remember {
        context.getSharedPreferences(GlassSurfaceView.PREFS_NAME, Context.MODE_PRIVATE)
    }
    var glassLandscape by remember {
        mutableStateOf(prefs.getBoolean(GlassSurfaceView.KEY_LANDSCAPE, false))
    }

    // 换壁纸：选一张图存进私有目录，backdropKey 一变就重渲染整页背景。
    // 选图三档回退与玻璃页一致（见 GlassScreen）：系统相册选择器 → 图库 ACTION_PICK → 文件选择器。
    var backdropKey by remember { mutableStateOf(0) }
    var wallpaperMessage by remember { mutableStateOf<String?>(null) }
    fun applyWallpaper(uri: android.net.Uri?) {
        if (uri == null) return
        wallpaperMessage = if (saveWallpaper(context, uri)) "壁纸已更新" else "壁纸读取失败"
        backdropKey++
    }
    val photoPicker = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { applyWallpaper(it) }
    val galleryPicker = rememberLauncherForActivityResult(PickFromGallery()) { applyWallpaper(it) }
    val documentPicker = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { applyWallpaper(it) }
    fun launchGallery() {
        try {
            galleryPicker.launch(Unit)
        } catch (e: android.content.ActivityNotFoundException) {
            documentPicker.launch("image/*")
        }
    }
    val mediaPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) launchGallery() else documentPicker.launch("image/*")
    }
    fun chooseWallpaper() {
        val permission = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            Manifest.permission.READ_MEDIA_IMAGES
        } else {
            Manifest.permission.READ_EXTERNAL_STORAGE
        }
        when {
            ActivityResultContracts.PickVisualMedia.isPhotoPickerAvailable(context) ->
                photoPicker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly))
            ContextCompat.checkSelfPermission(context, permission) ==
                PackageManager.PERMISSION_GRANTED -> launchGallery()
            else -> mediaPermission.launch(permission)
        }
    }

    DuoPage(Modifier.fillMaxSize(), backdropKey = backdropKey) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                // edge-to-edge 下必须让开系统栏，否则内容会压在状态栏上；
                // 让开多少由系统按机型（状态栏高度、刘海、手势条）给出。
                .systemBarsPadding()
                .verticalScroll(scroll)
                .padding(horizontal = 14.dp, vertical = 10.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            // 页头：对齐电脑端的 header —— 标题 + 一行副标题，不套边框。
            Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(
                    text = "Android DUO",
                    fontSize = 22.sp,
                    fontWeight = FontWeight.SemiBold,
                    letterSpacing = 2.sp,
                    color = Duo.Text,
                )
                Text(
                    text = "手机作为铰链角度源 · 驱动电脑端悬浮玻璃",
                    fontSize = 12.sp,
                    color = Duo.TextFaint,
                )
            }

            Spacer(Modifier.height(2.dp))

            HeroReadout(state)

            DuoPillButton(
                text = "开始",
                onClick = onOpenGlass,
                prominent = true,
                height = 50.dp,
            )

            DuoCard(title = "角度与共享") {
                SwitchRow(
                    label = "玻璃页横屏",
                    hint = "（本页始终竖屏）",
                    checked = glassLandscape,
                ) {
                    glassLandscape = it
                    prefs.edit().putBoolean(GlassSurfaceView.KEY_LANDSCAPE, it).apply()
                }
                SwitchRow(label = "发送角度到电脑", checked = sending) { onToggleSending() }

                if (sending && shareAddress != null) {
                    Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                        Text(
                            text = shareAddress,
                            fontSize = 24.sp,
                            fontWeight = FontWeight.Bold,
                            fontFamily = FontFamily.Monospace,
                            color = Duo.Accent,
                        )
                        Text(
                            text = "把这个地址填进电脑端 Windows Duo 的「手机 IP」",
                            fontSize = 11.sp,
                            color = Duo.TextDim,
                        )
                    }
                }
                DuoPillButton(
                    text = "添加壁纸",
                    onClick = { chooseWallpaper() },
                    height = 44.dp,
                )
                wallpaperMessage?.let {
                    Text(text = it, fontSize = 11.sp, color = Duo.TextDim)
                }
            }

            DuoCard(title = "状态") {
                val status = state.status
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    DuoChip(
                        label = "角度源",
                        value = status?.sourceName?.let {
                            if (status.usingSystemGravity) "重力水平仪" else "加速度计"
                        } ?: "无",
                        modifier = Modifier.weight(1f),
                    )
                    DuoChip(
                        label = "采样",
                        value = status?.let { "${it.sampleRateHz.toInt()} Hz" } ?: "-",
                        modifier = Modifier.weight(1f),
                    )
                    DuoChip(
                        label = "可观测",
                        value = "%.2f".format(state.observability),
                        modifier = Modifier.weight(1f),
                    )
                }
            }

            DuoCard(title = "角度曲线（最近约 2.4 秒）") {
                TraceGraph(state)
            }

            DuoPillButton(
                text = if (state.logging) "停止记录 CSV" else "开始记录 CSV（原始数据）",
                onClick = onToggleLogging,
                overlay = if (state.logging) Duo.Danger else null,
                height = 50.dp,
            )

            state.message?.let {
                Text(text = it, fontSize = 12.sp, color = Duo.TextDim)
            }
            state.lastLogPath?.let {
                Text(text = "最近日志：$it", fontSize = 12.sp, color = Duo.TextDim)
            }

            Spacer(Modifier.height(14.dp))
        }
    }
}

/** hero 玻璃块里的超大字角度读数。 */
@Composable
private fun HeroReadout(state: HingeUiState) {
    GlassSurface(Modifier.fillMaxWidth(), mist = 1f) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(vertical = 10.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            Row(verticalAlignment = Alignment.Bottom) {
                Text(
                    text = "%.1f".format(state.angleDeg),
                    fontSize = 84.sp,
                    fontWeight = FontWeight.Bold,
                    fontFamily = FontFamily.Monospace,
                    color = if (state.observability < 0.3) Duo.Danger else Duo.Text,
                )
                Text(
                    text = "°",
                    fontSize = 34.sp,
                    fontWeight = FontWeight.Bold,
                    color = Duo.Accent,
                    modifier = Modifier.padding(bottom = 12.dp),
                )
            }
            Text(
                text = SensorStatusText.describe(state) + " · " + state.phaseText,
                fontSize = 12.sp,
                color = Duo.TextFaint,
            )
        }
    }
}

@Composable
private fun SwitchRow(label: String, hint: String? = null, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        Switch(
            checked = checked,
            onCheckedChange = onChange,
            colors = SwitchDefaults.colors(
                checkedTrackColor = Duo.Accent,
                checkedThumbColor = Duo.Text,
                uncheckedTrackColor = Duo.Chrome,
                uncheckedThumbColor = Duo.TextDim,
                uncheckedBorderColor = Duo.ChromeBorder,
            ),
        )
        Text(text = label, fontSize = 14.sp, color = Duo.Text)
        if (hint != null) {
            Text(text = hint, fontSize = 11.sp, color = Duo.TextFaint)
        }
    }
}

/** 最近 240 个采样点（约 2.4 秒）的角度曲线。 */
@Composable
private fun TraceGraph(state: HingeUiState) {
    val data = state.trace
    Box(Modifier.fillMaxWidth()) {
        Canvas(
            modifier = Modifier
                .fillMaxWidth()
                .height(72.dp),
        ) {
            val w = size.width
            val h = size.height
            if (data.size < 2) return@Canvas

            val dataMin = data.min()
            val dataMax = data.max()
            // 窗口至少 1°、以数据中点为中心：读数平稳时曲线落在正中而不是贴底，
            // 数据跨度大时窗口跟着放大。
            val center = (dataMin + dataMax) / 2f
            val lo = min(center, dataMin - 0.05f) - 0.5f
            val hi = max(center, dataMax + 0.05f) + 0.5f
            val span = hi - lo

            val path = Path()
            data.forEachIndexed { i, v ->
                val x = w * i / (data.size - 1).toFloat()
                val y = (h - (v - lo) / span * h).coerceIn(1f, h - 1f)
                if (i == 0) path.moveTo(x, y) else path.lineTo(x, y)
            }
            drawPath(path, color = Duo.Accent, style = Stroke(width = 3f))

            // 45° / 50° 锁定阈值参考线，便于观察迟滞切换
            listOf(45f, 50f).forEach { threshold ->
                if (threshold in lo..hi) {
                    val y = h - (threshold - lo) / span * h
                    drawLine(
                        color = Color(0x55FF8A80),
                        start = Offset(0f, y),
                        end = Offset(w, y),
                        strokeWidth = 1f,
                    )
                }
            }
        }
    }
    Text(
        text = "范围 ${"%.1f".format(data.minOrNull() ?: 0f)}° ~ ${"%.1f".format(data.maxOrNull() ?: 0f)}°",
        fontSize = 11.sp,
        color = Duo.TextFaint,
    )
}
