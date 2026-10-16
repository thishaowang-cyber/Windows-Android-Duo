package com.example.laptophinge.ui

import android.Manifest
import android.app.Activity
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.ContextWrapper
import android.content.Intent
import android.content.pm.ActivityInfo
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Build
import android.provider.MediaStore
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContract
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import com.example.laptophinge.HingeUiState
import com.example.laptophinge.glass.GlassSurfaceView
import kotlin.math.abs
import kotlin.math.roundToInt

/** 源图最长边解码上限，避免整张大图进显存。 */
private const val MAX_SOURCE_DIMENSION = 2048

/**
 * 玻璃透视效果页。
 *
 * 传感器角度直接驱动效果：`倾角 = (1 − 开合角 / 展开终点) × 80°`。
 * 未接传感器时可用滑块手动模拟同一路输入，方便脱离笔记本夹具调试观感。
 */
@Composable
fun GlassScreen(
    state: HingeUiState,
    onBack: () -> Unit,
) {
    val context = LocalContext.current
    val prefs = remember {
        context.getSharedPreferences(GlassSurfaceView.PREFS_NAME, Context.MODE_PRIVATE)
    }

    // 展开终点固定 90°（屏幕竖直）：合盖时效果最强，到 90° 归零，不再让用户标定。
    val openAngle = 90.0
    var useSensor by rememberSaveable { mutableStateOf(true) }
    var simulatedAngle by rememberSaveable { mutableStateOf(90.0) }
    var frost by rememberSaveable { mutableStateOf(GlassSurfaceView.DEFAULT_FROST.toDouble()) }

    // 眼高不再是可调参数：它等于观看距离 ÷ 屏幕物理高度，由几何决定。
    val derivedEye = remember { GlassSurfaceView.deriveEyeDistance(context) }
    var sourceLabel by remember { mutableStateOf<String?>(null) }
    var surfaceView by remember { mutableStateOf<GlassSurfaceView?>(null) }

    // 未裁切的原始图留一份：横竖屏切换会改变屏幕比例，需要按新比例重裁重传，
    // 否则 aspect-fill 会把画面裁掉一大块。
    var originalSource by remember { mutableStateOf<Bitmap?>(null) }
    var croppedAspect by remember { mutableStateOf(0f) }

    // 选好图后进入沉浸全屏：控件与系统栏全部隐藏，画面上不压任何东西。
    // 点一下画面可以把控件叫回来。
    var showControls by remember { mutableStateOf(true) }

    val rootView = LocalView.current
    DisposableEffect(showControls) {
        val controller = rootView.context.findActivity()?.window
            ?.let { WindowCompat.getInsetsController(it, rootView) }
        if (showControls) {
            controller?.show(WindowInsetsCompat.Type.systemBars())
        } else {
            controller?.systemBarsBehavior =
                WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            controller?.hide(WindowInsetsCompat.Type.systemBars())
        }
        onDispose { controller?.show(WindowInsetsCompat.Type.systemBars()) }
    }

    // 横竖屏由主页上的开关决定（见 AngleScreen）。只有进入本页才切方向，离开就还原成
    // 竖屏，所以主页一定保持竖屏。用 SENSOR_LANDSCAPE 而不是 LANDSCAPE，
    // 这样手机两个方向横过来都能正立。
    DisposableEffect(Unit) {
        val activity = rootView.context.findActivity()
        val landscape = prefs.getBoolean(GlassSurfaceView.KEY_LANDSCAPE, false)
        activity?.requestedOrientation = if (landscape) {
            ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
        } else {
            ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
        }
        onDispose {
            activity?.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
        }
    }

    fun applyPicked(uri: Uri?) {
        if (uri == null) return
        val decoded = decodeDownscaled(context, uri, MAX_SOURCE_DIMENSION)
        if (decoded == null) {
            sourceLabel = "解码失败"
            return
        }
        // 源图必须与屏幕同比例。否则着色器里的 aspect-fill 会把画面放大裁切，
        // 倾斜时本该露出的黑边在水平方向被永久遮住，透视就看不出来了。
        originalSource = decoded
        val aspect = surfaceAspect(surfaceView, context)
        val cropped = cropToAspect(decoded, aspect, recycleSource = false)
        surfaceView?.setSourceBitmap(cropped)
        croppedAspect = aspect
        sourceLabel = "${cropped.width}×${cropped.height}"
        showControls = false
    }

    // 选图三档回退：
    //  1. 系统相册选择器（Android 11+/13+，免权限，这就是"图库"的现代形态）
    //  2. 图库 ACTION_PICK —— 部分 ROM 把 ACTION_OPEN_DOCUMENT 的默认处理者设成文件
    //     管理器，所以不能只靠文档选择器；ACTION_PICK 直接找图库。需要读媒体权限。
    //  3. 文件选择器 —— 最后的兜底。
    val photoPicker = rememberLauncherForActivityResult(
        ActivityResultContracts.PickVisualMedia(),
    ) { applyPicked(it) }
    val galleryPicker = rememberLauncherForActivityResult(
        PickFromGallery(),
    ) { applyPicked(it) }
    val documentPicker = rememberLauncherForActivityResult(
        ActivityResultContracts.GetContent(),
    ) { applyPicked(it) }

    // 极少数精简 ROM 不带相册应用，ACTION_PICK 会抛 ActivityNotFoundException；
    // 这里兜到文件选择器，避免直接崩。注意不能用 resolveActivity 预判——
    // Android 11+ 的包可见性限制会让它在没声明 <queries> 时误报 null。
    fun launchGallery() {
        try {
            galleryPicker.launch(Unit)
        } catch (e: ActivityNotFoundException) {
            documentPicker.launch("image/*")
        }
    }

    val mediaPermission = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) { granted -> if (granted) launchGallery() else documentPicker.launch("image/*") }

    fun chooseImage() {
        val permission = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            Manifest.permission.READ_MEDIA_IMAGES
        } else {
            Manifest.permission.READ_EXTERNAL_STORAGE
        }
        when {
            ActivityResultContracts.PickVisualMedia.isPhotoPickerAvailable(context) ->
                photoPicker.launch(
                    PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly),
                )
            ContextCompat.checkSelfPermission(context, permission) ==
                PackageManager.PERMISSION_GRANTED -> launchGallery()
            else -> mediaPermission.launch(permission)
        }
    }

    BackHandler(onBack = onBack)

    val liveAngle = if (useSensor) state.angleDeg else simulatedAngle
    val remaining = (1.0 - liveAngle / openAngle).coerceIn(0.0, 1.0)
    val tilt = remaining * GlassSurfaceView.MAX_TILT_DEG

    Box(Modifier.fillMaxSize()) {

        AndroidView(
            factory = { ctx ->
                GlassSurfaceView(ctx).apply {
                    surfaceView = this
                    // 横竖屏切换后按新比例重裁源图，否则画面会被裁掉一大块。
                    addOnLayoutChangeListener { v, _, _, _, _, _, _, _, _ ->
                        val aspect = if (v.height > 0) v.width.toFloat() / v.height else 0f
                        val src = originalSource
                        if (src != null && aspect > 0f && abs(aspect - croppedAspect) > 0.01f) {
                            val recropped = cropToAspect(src, aspect, recycleSource = false)
                            setSourceBitmap(recropped)
                            croppedAspect = aspect
                            sourceLabel = "${recropped.width}×${recropped.height}"
                        }
                    }
                }
            },
            update = { view ->
                view.angleDeg = liveAngle
                view.openAngleDeg = openAngle
                view.frost = frost.toFloat()
            },
            modifier = Modifier.fillMaxSize(),
        )

        // 点画面切换控件显隐。控件叠加在最上层，会先吃到落在自己身上的点击。
        Box(
            Modifier
                .fillMaxSize()
                .pointerInput(Unit) {
                    detectTapGestures { showControls = !showControls }
                },
        )

        if (showControls) {
            if (sourceLabel == null) {
                Text(
                    text = "选好图后画面会全屏显示，点屏幕可再叫出控件",
                    style = MaterialTheme.typography.bodyMedium,
                    color = Color.White.copy(alpha = 0.75f),
                    // 贴在顶部读数条下方，横屏时不会和底部面板重叠。
                    modifier = Modifier
                        .align(Alignment.TopCenter)
                        .padding(top = 104.dp),
                )
            }

            Row(
                modifier = Modifier
                    .align(Alignment.TopCenter)
                    .fillMaxWidth()
                    .padding(12.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                OutlinedButton(onClick = onBack) { Text("返回", fontSize = 13.sp) }
                Spacer(Modifier.width(10.dp))
                Card(
                    colors = CardDefaults.cardColors(
                        containerColor = MaterialTheme.colorScheme.surface.copy(alpha = 0.86f),
                    ),
                ) {
                    Row(
                        modifier = Modifier.padding(horizontal = 12.dp, vertical = 6.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Text(
                            text = String.format("%.1f°", liveAngle),
                            fontFamily = FontFamily.Monospace,
                            fontSize = 16.sp,
                            color = MaterialTheme.colorScheme.primary,
                        )
                        Spacer(Modifier.width(10.dp))
                        Text(
                            text = if (useSensor) "传感器" else "模拟",
                            style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                        Spacer(Modifier.width(10.dp))
                        Text(
                            text = "倾角 ${"%.1f".format(tilt)}°",
                            style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }

            Card(
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .fillMaxWidth()
                    .padding(12.dp)
                    .background(Color.Transparent, RoundedCornerShape(18.dp)),
                colors = CardDefaults.cardColors(
                    containerColor = MaterialTheme.colorScheme.surface.copy(alpha = 0.9f),
                ),
            ) {
                Column(
                    // 可滚动：矮屏或横屏下控件不会溢出屏幕。
                    modifier = Modifier
                        .padding(14.dp)
                        .verticalScroll(rememberScrollState()),
                    verticalArrangement = Arrangement.spacedBy(6.dp),
                ) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Button(onClick = { chooseImage() }) { Text("选择图片", fontSize = 13.sp) }
                        Spacer(Modifier.width(10.dp))
                        Text(
                            text = sourceLabel ?: "未选图",
                            style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }

                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Switch(checked = useSensor, onCheckedChange = { useSensor = it })
                        Spacer(Modifier.width(6.dp))
                        Text("实时铰链", style = MaterialTheme.typography.labelMedium)
                        Spacer(Modifier.width(14.dp))
                        Text(
                            text = "终点 90°",
                            style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }

                    if (!useSensor) {
                        LabelledSlider(
                            label = "模拟角度",
                            value = simulatedAngle,
                            range = 0f..180f,
                            display = "${simulatedAngle.roundToInt()}°",
                            onChange = { simulatedAngle = it.toDouble() },
                        )
                    }
                    LabelledSlider(
                        label = "磨砂",
                        value = frost,
                        range = 0f..0.18f,
                        display = "%.3f".format(frost),
                        onChange = { frost = it.toDouble() },
                    )
                    Text(
                        text = "眼距 %.2f 倍屏高  =  观看距离 %.0f cm ÷ 屏幕物理高度（由几何算出，不可调）"
                            .format(derivedEye, GlassSurfaceView.VIEWING_DISTANCE_M * 100),
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        }
    }
}

@Composable
private fun LabelledSlider(
    label: String,
    value: Double,
    range: ClosedFloatingPointRange<Float>,
    display: String,
    onChange: (Float) -> Unit,
) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text(
            text = label,
            style = MaterialTheme.typography.labelMedium,
            modifier = Modifier.width(64.dp),
        )
        Slider(
            value = value.toFloat(),
            onValueChange = onChange,
            valueRange = range,
            modifier = Modifier.weight(1f),
        )
        Text(
            text = display,
            style = MaterialTheme.typography.labelSmall,
            fontFamily = FontFamily.Monospace,
            modifier = Modifier.width(52.dp),
        )
    }
}

/**
 * 两遍解码：先读尺寸算出采样率，再按需下采样解码，避免把整张原图读进内存。
 */
private fun decodeDownscaled(context: Context, uri: Uri, maxDimension: Int): Bitmap? {
    val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
    context.contentResolver.openInputStream(uri)?.use {
        BitmapFactory.decodeStream(it, null, bounds)
    }
    if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null

    var sample = 1
    while (bounds.outWidth / sample > maxDimension || bounds.outHeight / sample > maxDimension) {
        sample *= 2
    }
    val options = BitmapFactory.Options().apply {
        inSampleSize = sample
        inPreferredConfig = Bitmap.Config.ARGB_8888
    }
    return context.contentResolver.openInputStream(uri)?.use {
        BitmapFactory.decodeStream(it, null, options)
    }
}

/** 直接找图库要一张图。ACTION_PICK 会命中相册应用，不像文档选择器会被文件管理器接管。 */
internal class PickFromGallery : ActivityResultContract<Unit, Uri?>() {
    override fun createIntent(context: Context, input: Unit): Intent =
        Intent(Intent.ACTION_PICK, MediaStore.Images.Media.EXTERNAL_CONTENT_URI)
            .setType("image/*")

    override fun parseResult(resultCode: Int, intent: Intent?): Uri? =
        intent?.takeIf { resultCode == Activity.RESULT_OK }?.data
}

/** Compose 的 LocalView.context 可能是包装过的，逐层解开拿 Activity。 */
private tailrec fun Context.findActivity(): Activity? = when (this) {
    is Activity -> this
    is ContextWrapper -> baseContext.findActivity()
    else -> null
}

/** 渲染表面的宽高比。布局未完成时退回屏幕尺寸。 */
private fun surfaceAspect(view: GlassSurfaceView?, context: Context): Float {
    if (view != null && view.height > 0) return view.width.toFloat() / view.height
    val metrics = context.resources.displayMetrics
    return if (metrics.heightPixels > 0) {
        metrics.widthPixels.toFloat() / metrics.heightPixels
    } else {
        9f / 20f
    }
}

/** 居中裁切到目标比例。比例已经一致时原样返回，不产生额外拷贝。 */
private fun cropToAspect(bitmap: Bitmap, aspect: Float, recycleSource: Boolean = true): Bitmap {
    val current = bitmap.width.toFloat() / bitmap.height
    if (aspect <= 0f || abs(current - aspect) < 0.01f) return bitmap
    val cropped = if (current > aspect) {
        val targetWidth = (bitmap.height * aspect).roundToInt().coerceIn(1, bitmap.width)
        Bitmap.createBitmap(bitmap, (bitmap.width - targetWidth) / 2, 0, targetWidth, bitmap.height)
    } else {
        val targetHeight = (bitmap.width / aspect).roundToInt().coerceIn(1, bitmap.height)
        Bitmap.createBitmap(bitmap, 0, (bitmap.height - targetHeight) / 2, bitmap.width, targetHeight)
    }
    if (cropped !== bitmap && recycleSource) bitmap.recycle()
    return cropped
}
