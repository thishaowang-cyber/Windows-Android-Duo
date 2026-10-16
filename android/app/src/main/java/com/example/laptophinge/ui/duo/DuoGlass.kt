package com.example.laptophinge.ui.duo

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.RadialGradient
import android.graphics.Shader
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asAndroidBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.layout.positionInWindow
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.exp
import kotlin.math.hypot
import kotlin.math.max
import kotlin.math.min
import kotlin.math.pow
import kotlin.math.roundToInt
import kotlin.math.sin

/**
 * DUO 的玻璃视觉层，逐条对齐电脑端 Windows Duo 首页
 * （`WindowsDuo/App/Theme.cs`、`PageGlass.cs`、`Shaders/Glass.hlsl` 的 `PSLiquidGlass`）。
 *
 * ## 为什么尺寸取比例而不是照抄像素
 *
 * 电脑端的常数是 CSS 像素（96dpi）：hero 块宽约 713px、圆角 90px。90px 的物理尺寸是
 * 0.94 英寸，照搬到 267dp 宽的手机上会占掉屏幕三分之一，在 640dp 宽的平板上占掉一半，
 * 结果是两个椭圆而不是一块玻璃。要移植的是**观感比例** `radius / 面板宽 = 90/713 = 12.6%`；
 * 比例无量纲，直接乘板宽，任何屏幕上都长一样。
 *
 * 例外是发丝亮线：着色器里它用绝对单位（距边 2–5px），因为它表达的是抛光倒角上的一条
 * 反光，宽度不该随卡片大小变化。CSS px 换 dp 是 ×0.6。
 */
object Duo {

    val Accent = Color(0xFF8B7CF7)
    val Text = Color(0xFFFFFFFF)
    val TextDim = Color(0xFFC4C6D6)
    val TextFaint = Color(0xFF969AAC)

    /** 参考站控件面板 rgba(22,22,26,.72) 叠在暗背景上的合成结果。 */
    val Chrome = Color(0xB3211F28)
    val ChromeBorder = Color(0x16FFFFFF)
    val Danger = Color(0xFFFF6B63)

    // PSLiquidGlass 的 Block 参数，除以电脑端 713px 的块宽得到比例。
    const val RADIUS_RATIO = 0.126f     // 90
    const val BEZEL_RATIO = 0.056f      // 40
    const val SHADOW_REACH = 0.084f     // 60
    const val SHADOW_SIGMA = 0.040f     // sqrt(800) ≈ 28.3
    const val SPECULAR = 0.22f
    const val TINT = 0.04f
    const val SHADOW = 0.5f
    const val CARD_RADIUS_RATIO = 0.10f
    const val CARD_BEZEL_RATIO = 0.045f

    /** 归一化光向 (0.5, -0.7)：|N·L| 使上/下边的 sheen 比左/右边亮。 */
    const val LIGHT_X = 0.581f
    const val LIGHT_Y = -0.814f

    /** 按钮填充 = 色斑场 ×0.62（电脑端 GlassControls 的 Depth 常数）。 */
    const val BUTTON_DEPTH = 0.62f

    val FieldTop = 0xFF2E1A4A.toInt()
    val FieldBottom = 0xFF101438.toInt()
    const val FIELD_ANGLE_DEG = 115.0

    /** cx, cy, radius ÷ max(场边)，ARGB —— 与 Theme.PaintField 逐项一致。 */
    val BLOBS = listOf(
        floatArrayOf(0.10f, 0.06f, 0.78f) to 0xC660B0F7.toInt(),
        floatArrayOf(0.78f, 0.16f, 0.62f) to 0xCDE04E96.toInt(),
        floatArrayOf(0.52f, 0.86f, 0.70f) to 0xBE4AC8D6.toInt(),
        floatArrayOf(0.94f, 0.74f, 0.52f) to 0xAAF6A85C.toInt(),
        floatArrayOf(0.30f, 0.52f, 0.46f) to 0x8C7E6EF6.toInt(),
    )
}

/** 用户自选壁纸：取一次、拷进应用私有目录，之后每次渲染读这张本地文件。 */
const val BACKDROP_PREFS = "duo_backdrop"
const val KEY_BACKDROP_URI = "backdrop_uri"

/** 把用户选的图持久化：content:// 的读权限会随重启失效，所以原样拷一份到私有目录。 */
fun saveWallpaper(context: Context, uri: android.net.Uri): Boolean {
    val file = java.io.File(context.filesDir, "wallpaper.img")
    val copied = runCatching {
        context.contentResolver.openInputStream(uri)?.use { input ->
            file.outputStream().use { input.copyTo(it) }
        } != null
    }.getOrDefault(false)
    if (!copied) return false
    context.getSharedPreferences(BACKDROP_PREFS, Context.MODE_PRIVATE)
        .edit().putString(KEY_BACKDROP_URI, uri.toString()).apply()
    return true
}

/** 两遍解码：先量尺寸算采样率，控制在 2400 以内，再真正解码。 */
fun loadWallpaper(context: Context): Bitmap? {
    val file = java.io.File(context.filesDir, "wallpaper.img")
    if (!file.exists()) return null
    return runCatching {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(file.absolutePath, bounds)
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return@runCatching null
        var sample = 1
        while (bounds.outWidth / sample > 2400 || bounds.outHeight / sample > 2400) sample *= 2
        BitmapFactory.decodeFile(
            file.absolutePath,
            BitmapFactory.Options().apply { inSampleSize = sample; inPreferredConfig = Bitmap.Config.ARGB_8888 },
        )
    }.getOrNull()
}

/**
 * 背景：优先用户自选壁纸（一张真实照片 centerCrop 到页面尺寸 + 压暗保证白字可读），
 * 没有自选壁纸时回退到程序画的色斑场。玻璃必须有细节可磨砂、可折射才读得出来
 * （参考站的原话：平色背景上玻璃无从显现），所以想看到真正的玻璃效果请点"添加壁纸"。
 * 整页只渲染成一张位图一次，每块玻璃从里面取自己在窗口位置上的那一片，于是板内板外
 * 是同一张图。取错原点会在板边留下一条断层——电脑端踩过这个坑。
 */
fun buildBackdrop(context: Context, width: Int, height: Int): ImageBitmap {
    val w = width.coerceAtLeast(1)
    val h = height.coerceAtLeast(1)
    val bmp = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888)
    val canvas = android.graphics.Canvas(bmp)
    val paint = Paint(Paint.ANTI_ALIAS_FLAG)

    val photo = loadWallpaper(context)
    if (photo != null) {
        // centerCrop：短边贴满、长边居中裁切。
        val scale = max(w.toFloat() / photo.width, h.toFloat() / photo.height)
        val sw = (w / scale).toInt().coerceAtLeast(1)
        val sh = (h / scale).toInt().coerceAtLeast(1)
        canvas.drawBitmap(
            photo,
            android.graphics.Rect((photo.width - sw) / 2, (photo.height - sh) / 2, (photo.width + sw) / 2, (photo.height + sh) / 2),
            android.graphics.Rect(0, 0, w, h),
            paint,
        )
        // 压暗 + 轻微冷色，让白色正文和紫色强调在任意照片上都保持对比。
        canvas.drawColor(0x66101020.toInt())
        paint.shader = null
        return bmp.asImageBitmap()
    }

    val angle = Math.toRadians(Duo.FIELD_ANGLE_DEG)
    val dx = cos(angle).toFloat()
    val dy = sin(angle).toFloat()
    val half = hypot(w.toFloat(), h.toFloat()) / 2f
    paint.shader = LinearGradient(
        w / 2f - dx * half, h / 2f - dy * half, w / 2f + dx * half, h / 2f + dy * half,
        Duo.FieldTop, Duo.FieldBottom, Shader.TileMode.CLAMP,
    )
    canvas.drawRect(0f, 0f, w.toFloat(), h.toFloat(), paint)

    val unit = max(w, h)
    for ((pos, colour) in Duo.BLOBS) {
        val r = unit * pos[2]
        paint.shader = RadialGradient(
            w * pos[0], h * pos[1], r,
            intArrayOf(colour, colour and 0x00FFFFFF),
            floatArrayOf(0f, 1f),
            Shader.TileMode.CLAMP,
        )
        canvas.drawCircle(w * pos[0], h * pos[1], r, paint)
    }
    paint.shader = null
    return bmp.asImageBitmap()
}

/** 页面背景场的像素尺寸与位图；根节点写入，所有玻璃块据此取自己的切片。 */
val LocalBackdropSize = staticCompositionLocalOf { IntSize.Zero }
val LocalBackdropImage = staticCompositionLocalOf<ImageBitmap?> { null }

/** 同一张场重度降采样再放大回来的磨砂版；玻璃切片取这张，板内板外才有锐/糊之分。 */
val LocalFrostImage = staticCompositionLocalOf<ImageBitmap?> { null }

/**
 * down/up-scale 模糊，与电脑端"backdrop 渲染一次、重度 down/up-scale 模糊一次"同一配方：
 * 双线性重采样近似高斯，1/12 的降采样率对应参考站 blur(40px) 量级的观感。
 */
private fun frostBackdrop(field: ImageBitmap): ImageBitmap {
    val src = field.asAndroidBitmap()
    val p = Paint(Paint.FILTER_BITMAP_FLAG)
    val sw = (src.width / 12).coerceAtLeast(1)
    val sh = (src.height / 12).coerceAtLeast(1)
    val small = Bitmap.createBitmap(sw, sh, Bitmap.Config.ARGB_8888)
    android.graphics.Canvas(small).drawBitmap(
        src,
        android.graphics.Rect(0, 0, src.width, src.height),
        android.graphics.Rect(0, 0, sw, sh),
        p,
    )
    val out = Bitmap.createBitmap(src.width, src.height, Bitmap.Config.ARGB_8888)
    android.graphics.Canvas(out).drawBitmap(
        small,
        android.graphics.Rect(0, 0, sw, sh),
        android.graphics.Rect(0, 0, src.width, src.height),
        p,
    )
    return out.asImageBitmap()
}

/** 一页 DUO：铺底色斑场，并把场（清晰 + 磨砂两版）以位图 + 尺寸放进子树。 */
@Composable
fun DuoPage(modifier: Modifier = Modifier, backdropKey: Any? = Unit, content: @Composable BoxScope.() -> Unit) {
    val context = LocalContext.current
    var pageSize by remember { mutableStateOf(IntSize.Zero) }
    val field = remember(pageSize, backdropKey) {
        if (pageSize.width > 0 && pageSize.height > 0) buildBackdrop(context, pageSize.width, pageSize.height) else null
    }
    val frost = remember(field) { field?.let { frostBackdrop(it) } }
    CompositionLocalProvider(
        LocalBackdropSize provides pageSize,
        LocalBackdropImage provides field,
        LocalFrostImage provides frost,
    ) {
        Box(modifier.onSizeChanged { pageSize = it }) {
            val f = field
            if (f != null) {
                Canvas(Modifier.matchParentSize()) {
                    drawImage(f, dstSize = IntSize(size.width.roundToInt(), size.height.roundToInt()))
                }
            } else {
                // 第一帧还没量到尺寸，先用场底色铺着，避免出现一整片黑。
                Box(Modifier.matchParentSize().background(Color(0xFF2E1A4A)))
            }
            content()
        }
    }
}

/** 投影画在玻璃轮廓之外，所以玻璃层四周留出这一圈；按视口宽度定，各块玻璃一致。 */
@Composable
fun duoShadowSpace(): Dp {
    val field = LocalBackdropSize.current
    val density = LocalDensity.current
    return remember(field, density) {
        if (field.width <= 0) 10.dp
        else (field.width * Duo.SHADOW_REACH / density.density).roundToInt().coerceIn(6, 22).dp
    }
}

private fun smoothstep(x: Float): Float {
    val t = x.coerceIn(0f, 1f)
    return t * t * (3f - 2f * t)
}

/**
 * sheen 沿倒角深度的剖面 `pow(|N·L|·(1 − smoothstep(0, 0.4·bezel, dist)), 1.5)`：
 * dist=0 最亮，走到 0.4·bezel 归零，所以高光是一条很窄的边亮，而不是整块发白。
 * 列表按"贴边 → 向内"排序，画对侧那条边时要 reversed。
 */
private fun sheenStops(weight: Float, specular: Float, steps: Int): List<Color> =
    (0..steps).map { i ->
        val v = (weight * (1f - smoothstep(i.toFloat() / steps))).coerceAtLeast(0f)
        Color.White.copy(alpha = specular * v.pow(1.5f))
    }

/**
 * 一块玻璃。两层 Canvas：外层画投影（不能被裁，它在玻璃之外），内层画玻璃面并裁成圆角。
 *
 * 这一版不用 `PathShape` 做裁剪：Compose 1.9 的 ui-graphics 里没有这个类。改用
 * `Modifier.clip(RoundedCornerShape(...))`，它走图层轮廓、自带抗锯齿；半径要先量到板宽
 * 才算得出，所以第一帧可能短暂是直角。
 */
@Composable
fun GlassSurface(
    modifier: Modifier = Modifier,
    radiusRatio: Float = Duo.RADIUS_RATIO,
    bezelRatio: Float = Duo.BEZEL_RATIO,
    specular: Float = Duo.SPECULAR,
    tint: Float = Duo.TINT,
    shadow: Float = Duo.SHADOW,
    lens: Float = 0.06f,
    mist: Float = 0.0f,
    contentPadding: PaddingValues = PaddingValues(18.dp),
    content: @Composable () -> Unit,
) {
    val field = LocalBackdropImage.current
    val frost = LocalFrostImage.current ?: field
    val density = LocalDensity.current
    val space = duoShadowSpace()
    val spacePx = remember(space, density) { with(density) { space.toPx() } }
    var origin by remember { mutableStateOf(IntOffset.Zero) }
    var slab by remember { mutableStateOf(IntSize.Zero) }
    val radiusDp = remember(slab, density, radiusRatio) {
        (slab.width * radiusRatio / density.density).roundToInt().coerceAtLeast(1).dp
    }

    Box(
        modifier.onGloballyPositioned { coords ->
            val p = coords.positionInWindow()
            origin = IntOffset(p.x.roundToInt(), p.y.roundToInt())
        },
    ) {
        Canvas(Modifier.matchParentSize()) {
            if (shadow > 0f) {
                val w = size.width - spacePx * 2f
                val h = size.height - spacePx * 2f
                if (w >= 8f && h >= 8f) {
                    drawShadowRings(
                        at = Offset(spacePx, spacePx),
                        w = w,
                        h = h,
                        radius = slabRadius(w, h, radiusRatio),
                        reach = spacePx,
                        sigma = w * Duo.SHADOW_SIGMA,
                        shadow = shadow,
                    )
                }
            }
        }

        Box(
            Modifier
                .padding(space)
                .onSizeChanged { slab = it }
                .clip(RoundedCornerShape(radiusDp)),
        ) {
            Canvas(Modifier.matchParentSize()) {
                if (frost == null) return@Canvas
                val w = size.width
                val h = size.height
                if (w < 8f || h < 8f) return@Canvas
                val bezel = min(w * bezelRatio, h / 3f)
                // 磨砂 + 透镜放大：取比板子略大的一圈场缩进板内，中心放大、边缘被裁，
                // 就是厚玻璃把身后的东西"顶大一号"的样子。
                drawBackdropSlice(frost, origin.x + spacePx.roundToInt(), origin.y + spacePx.roundToInt(), IntSize(w.roundToInt(), h.roundToInt()), lens)
                if (tint > 0f) drawRect(Color.White.copy(alpha = tint))
                drawChamfer(w, h, bezel)
                drawSheen(w, h, bezel, specular)
                drawMistRing(w, h, slabRadius(w, h, radiusRatio), mist)
                drawInnerRim(w, h, slabRadius(w, h, radiusRatio), specular, density.density)
            }
            Box(Modifier.padding(contentPadding)) { content() }
        }
    }
}

private fun slabRadius(w: Float, h: Float, radiusRatio: Float) =
    min(min(w, h) / 2f * 0.9f, w * radiusRatio)

/**
 * 把背景场里正好在这块玻璃后面的那一片画进来。板比视口高时（滚出屏幕的那截）只取能取到的
 * 一段铺上去，误差全部落在看不见的区域。
 *
 * [lens] 是透镜放大量：源区比目标板大 (1+lens) 倍并居中，缩进板内，于是板中心的图案比板外
 * 放大一号——厚玻璃折射的观感。取的是磨砂场，所以放大同时不会把锐利细节带进板内。
 */
private fun DrawScope.drawBackdropSlice(
    field: ImageBitmap,
    slabX: Int,
    slabY: Int,
    dst: IntSize,
    lens: Float,
    alpha: Float = 1f,
) {
    val growX = (dst.width * lens / 2f).roundToInt()
    val growY = (dst.height * lens / 2f).roundToInt()
    val srcX = (slabX - growX).coerceIn(0, max(0, field.width - 1))
    val srcY = (slabY - growY).coerceIn(0, max(0, field.height - 1))
    drawImage(
        image = field,
        alpha = alpha,
        dstSize = dst,
        srcOffset = IntOffset(srcX, srcY),
        srcSize = IntSize(
            min(dst.width + growX * 2, field.width - srcX).coerceAtLeast(1),
            min(dst.height + growY * 2, field.height - srcY).coerceAtLeast(1),
        ),
    )
}

/**
 * 外投影：逐圈向外的高斯衰减暗环，叠加后等价于着色器的
 * `behind *= 1 − shadow·falloff·0.6`。
 *
 * 描边宽度取 2×step 让相邻圈重叠：不重叠时 alpha 按 8bit 量化，圈与圈之间会显出一圈圈
 * 台阶（用户看到的"框外一圈黑圈"）。重叠后抗锯齿把台阶抹平。
 */
private fun DrawScope.drawShadowRings(
    at: Offset,
    w: Float,
    h: Float,
    radius: Float,
    reach: Float,
    sigma: Float,
    shadow: Float,
) {
    val rings = 24
    val step = reach / rings
    for (i in rings downTo 1) {
        val d = i * step
        val a = (shadow * 0.6f * exp(-(d * d) / (2f * sigma * sigma)).toFloat() * 255f).roundToInt()
        if (a <= 0) continue
        drawRoundRect(
            color = Color.Black.copy(alpha = a / 255f),
            topLeft = at - Offset(d, d),
            size = Size(w + d * 2f, h + d * 2f),
            cornerRadius = CornerRadius(radius + d, radius + d),
            style = Stroke(width = step * 2f),
        )
    }
}

/** 倒角斜面比普通面暗：距边 0.6·bezel 内最多压暗 9%（`lerp(1, 0.7, innerShadow*0.3)`）。 */
private fun DrawScope.drawChamfer(w: Float, h: Float, bezel: Float) {
    val depth = bezel * 0.6f
    val darkest = Color.Black.copy(alpha = 0.09f)
    drawRect(Brush.verticalGradient(listOf(darkest, Color.Transparent)), size = Size(w, depth))
    drawRect(
        Brush.verticalGradient(listOf(Color.Transparent, darkest)),
        topLeft = Offset(0f, h - depth),
        size = Size(w, depth),
    )
    drawRect(Brush.horizontalGradient(listOf(darkest, Color.Transparent)), size = Size(depth, h))
    drawRect(
        Brush.horizontalGradient(listOf(Color.Transparent, darkest)),
        topLeft = Offset(w - depth, 0f),
        size = Size(depth, h),
    )
}

/**
 * sheen 高光带，只有 0.4·bezel 深。上/下边取 |N·L| = 0.814、左/右边取 0.5，且着色器取绝对值，
 * 所以上边与下边一样亮——"上下比两侧亮"正是这块玻璃能被认出来的原因。
 */
private fun DrawScope.drawSheen(w: Float, h: Float, bezel: Float, specular: Float) {
    val depth = bezel * 0.4f
    val horiz = sheenStops(abs(Duo.LIGHT_Y), specular, 8)
    val vert = sheenStops(abs(Duo.LIGHT_X), specular, 8)
    drawRect(Brush.verticalGradient(horiz), size = Size(w, depth))
    drawRect(Brush.verticalGradient(horiz.reversed()), topLeft = Offset(0f, h - depth), size = Size(w, depth))
    drawRect(Brush.horizontalGradient(vert), size = Size(depth, h))
    drawRect(Brush.horizontalGradient(vert.reversed()), topLeft = Offset(w - depth, 0f), size = Size(depth, h))
}

/**
 * 白雾环：贴玻璃内缘的一圈柔光，30 道递进描边、alpha ≈ 70·(1−t)^2.1 向外衰减变细，
 * 与电脑端 hero 画布外圈的雾环同一配方。它把硬边"化开"，是"生硬 → 玻璃"的关键一笔。
 */
private fun DrawScope.drawMistRing(w: Float, h: Float, radius: Float, strength: Float) {
    if (strength <= 0f) return
    val ring = min(w, h) * 0.09f
    val steps = 30
    val step = ring / steps
    for (i in 0 until steps) {
        val t = i.toFloat() / steps
        val a = (70f / 255f * (1f - t).pow(2.1f) * strength).coerceAtMost(1f)
        if (a <= 0.004f) break
        val inset = i * step
        drawRoundRect(
            color = Color.White.copy(alpha = a),
            topLeft = Offset(inset, inset),
            size = Size((w - inset * 2f).coerceAtLeast(1f), (h - inset * 2f).coerceAtLeast(1f)),
            cornerRadius = CornerRadius((radius - inset).coerceAtLeast(1f)),
            style = Stroke(width = step + 0.5f),
        )
    }
}

/** 距边 2–5px 的一条发丝亮线（绝对尺寸：CSS px × 0.6 = dp）。 */
private fun DrawScope.drawInnerRim(w: Float, h: Float, radius: Float, specular: Float, density: Float) {
    val inset = 2.5f * 0.6f * density
    val stroke = 3f * 0.6f * density
    drawRoundRect(
        // 亮线只在大块（hero）上才读得出是"抛光倒角"；小卡片上同样的 alpha 会变成一条
        // 明显的白框。按面积缩放：大面取满，小面淡到几乎看不见。
        color = Color.White.copy(alpha = rimAlpha(w, h, specular)),
        topLeft = Offset(inset, inset),
        size = Size(w - inset * 2f, h - inset * 2f),
        cornerRadius = CornerRadius((radius - inset).coerceAtLeast(1f)),
        style = Stroke(width = stroke),
    )
}

private fun rimAlpha(w: Float, h: Float, specular: Float): Float {
    val big = 300_000f  // 约等于 hero 在常见手机上的像素面积
    val area = w * h
    val scale = (area / big).coerceIn(0.12f, 1f)
    return ((0.06f + specular * 0.15f) * scale).coerceAtMost(0.22f)
}

/** 卡片：一块小玻璃，标题走参考站的 10px Bold 白。 */
@Composable
fun DuoCard(
    title: String? = null,
    modifier: Modifier = Modifier,
    contentPadding: PaddingValues = PaddingValues(start = 18.dp, end = 18.dp, top = 14.dp, bottom = 16.dp),
    content: @Composable ColumnScope.() -> Unit,
) {
    GlassSurface(
        modifier = modifier,
        radiusRatio = Duo.CARD_RADIUS_RATIO,
        bezelRatio = Duo.CARD_BEZEL_RATIO,
        shadow = 0.4f,
        contentPadding = PaddingValues(0.dp),
    ) {
        Column(Modifier.padding(contentPadding), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            if (title != null) {
                Text(title, fontSize = 14.sp, fontWeight = FontWeight.Bold, color = Duo.Text)
            }
            content()
        }
    }
}

/** 深色 chrome 小块：rgba(22,22,26,.72) + 白 8.6% 描边，给卡片里的 label/value 对用。 */
@Composable
fun DuoChip(
    label: String,
    value: String,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier
            .background(Duo.Chrome, RoundedCornerShape(12.dp))
            .border(1.dp, Duo.ChromeBorder, RoundedCornerShape(12.dp))
            .padding(horizontal = 12.dp, vertical = 7.dp),
        verticalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        Text(label, fontSize = 11.sp, color = Duo.TextFaint)
        Text(value, fontSize = 14.sp, color = Duo.Text)
    }
}

/**
 * 药丸按钮。填充和玻璃卡片同一配方：按窗口位置取身后那片磨砂背景，再整体压暗
 * （×0.62 的等价黑层），所以它是"坐在背景上的一块深色玻璃"，滚动时内容跟着位置走。
 */
@Composable
fun DuoPillButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    prominent: Boolean = false,
    height: Dp = 48.dp,
    overlay: Color? = null,
    enabled: Boolean = true,
) {
    val frost = LocalFrostImage.current ?: LocalBackdropImage.current
    var origin by remember { mutableStateOf(IntOffset.Zero) }
    var boxSize by remember { mutableStateOf(IntSize.Zero) }
    val pill = RoundedCornerShape(50)
    Box(
        modifier
            .fillMaxWidth()
            .height(height)
            .onGloballyPositioned { coords ->
                val p = coords.positionInWindow()
                origin = IntOffset(p.x.roundToInt(), p.y.roundToInt())
            }
            .onSizeChanged { boxSize = it }
            .clip(pill)
            .clickable(enabled = enabled, onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        if (frost != null && boxSize.width > 0) {
            Canvas(Modifier.matchParentSize()) {
                // 和玻璃卡片一样按窗口位置取身后那一片（磨砂版），按钮才是"坐在背景上"
                // 而不是"把整张背景缩进自己这么小一块"。整张缩放会让按钮内容与位置无关、
                // 滚动时纹丝不动——就是用户说的"不是实时渲染"。
                drawBackdropSlice(frost, origin.x, origin.y, boxSize, 0f, if (enabled) 1f else 0.59f)
                // 与电脑端 Depth 一致的压暗：原来是 RGB×0.62，黑覆盖层 alpha = 1−0.62 等价。
                drawRect(Color.Black.copy(alpha = 1f - Duo.BUTTON_DEPTH))
                if (overlay != null) drawRect(overlay.copy(alpha = 0.5f))
            }
        } else {
            Box(Modifier.matchParentSize().background(Duo.Chrome))
        }
        // 描边要画在填充之上，所以是独立的一层而不是 Canvas 里的最后一笔。
        Box(
            Modifier
                .matchParentSize()
                .border(1.dp, Color.White.copy(alpha = if (enabled) 0.35f else 0.16f), pill),
        )
        if (prominent) {
            Box(
                Modifier
                    .matchParentSize()
                    .padding(2.5.dp)
                    .border(2.dp, Duo.Accent.copy(alpha = 0.59f), pill),
            )
        }
        Text(
            text,
            fontSize = 16.sp,
            fontWeight = FontWeight.SemiBold,
            color = if (enabled) Duo.Text else Duo.Text.copy(alpha = 0.55f),
            modifier = Modifier.padding(horizontal = 20.dp),
        )
    }
}
