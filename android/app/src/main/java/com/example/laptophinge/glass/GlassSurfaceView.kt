package com.example.laptophinge.glass

import android.content.Context
import android.graphics.Bitmap
import android.opengl.EGL14
import android.opengl.EGLConfig
import android.opengl.EGLContext
import android.opengl.EGLDisplay
import android.opengl.EGLExt
import android.opengl.EGLSurface
import android.opengl.GLES30
import android.opengl.GLUtils
import android.util.AttributeSet
import android.util.Log
import android.view.SurfaceHolder
import android.view.SurfaceView
import java.util.concurrent.atomic.AtomicReference

/**
 * 玻璃透视效果的渲染表面。
 *
 * 渲染在独立线程 + 自建 EGL(ES 3.0) 上下文里跑，UI 线程只通过 volatile 字段和
 * 一个 [AtomicReference] 投递参数与源图，不做任何加锁。
 *
 * 角度映射沿用参考实现：`倾角 = (1 − 开合角 / 展开终点) × 80°`，
 * 即合盖时玻璃翘到最大，完全打开到标定终点时效果归零。
 */
class GlassSurfaceView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
) : SurfaceView(context, attrs), SurfaceHolder.Callback {

    /** 着色器与纹理都从这里读；用 applicationContext 避免持有 Activity。 */
    private val appContext: Context = context.applicationContext

    /** 当前开合角（0=合盖，90=垂直，180=摊平），单位度。UI 线程写，渲染线程读。 */
    @Volatile var angleDeg: Double = 120.0

    /** 展开终点：开合角达到它时效果归零。 */
    @Volatile var openAngleDeg: Double = 120.0

    /** 磨砂强度 0..0.18。 */
    @Volatile var frost: Float = DEFAULT_FROST

    /**
     * 眼距，单位是**屏幕高度**（观看距离 ÷ 屏幕物理高度）。
     *
     * 着色器里它决定远端收窄的比例 `w = 眼距 / (眼距 + 距离·sin θ)`：铰链边 w = 1
     * 完全不动，越远越窄。数值越大 = 人离得越远 = 透视越弱，所以它是让几何成立的
     * 物理量，不是观感参数，因此开机算一次，不交给滑杆。
     */
    @Volatile var eyeDistance: Float = deriveEyeDistance(context.applicationContext)

    /**
     * 把整幅画面旋转 180°。仅用于手机整体装反（内容方向也要跟着翻）的情况。
     */
    @Volatile var flip180: Boolean = false

    /**
     * 玻璃从**上边缘**掀起。默认 false：锚点在**下边缘**，也就是投影里被钉住不动的是
     * 下边——下端一比一不拉伸，形变（放大）向上端递增。界面不暴露该开关。
     */
    @Volatile var hingeAtTop: Boolean = false

    /**
     * 源图由视图持有。SurfaceView 被销毁重建时（切后台、旋屏）渲染线程会重开、
     * 纹理随之丢失，只有留着这张图才能把画面补回来。
     */
    private val sourceBitmap = AtomicReference<Bitmap?>(null)

    /** 待上传的源图。渲染线程取走后不回收——生命周期归 [sourceBitmap]。 */
    private val pendingBitmap = AtomicReference<Bitmap?>(null)

    private var renderThread: RenderThread? = null

    init {
        holder.addCallback(this)
    }

    /**
     * 提交一张源图。
     *
     * 刻意不回收被替换掉的那张：调用方可能还留着未裁切的原始图，用于屏幕比例变化
     * （横竖屏切换）时按新比例重裁，在这里回收会把它一并销毁。交给 GC。
     */
    fun setSourceBitmap(bitmap: Bitmap?) {
        sourceBitmap.set(bitmap)
        pendingBitmap.set(bitmap)
    }

    override fun surfaceCreated(holder: SurfaceHolder) {
        val thread = RenderThread()
        renderThread = thread
        thread.start()
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) = Unit

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        renderThread?.let {
            it.shutdown()
            it.join()
        }
        renderThread = null
    }

    /** 渲染线程读取的一帧参数快照。 */
    private data class FrameParams(
        val angleDeg: Double,
        val openAngleDeg: Double,
        val frost: Float,
        val eyeDistance: Float,
        val flip180: Boolean,
        val hingeAtTop: Boolean,
    )

    private fun snapshot() =
        FrameParams(angleDeg, openAngleDeg, frost, eyeDistance, flip180, hingeAtTop)

    private inner class RenderThread : Thread("glass-render") {

        @Volatile private var running = true

        private var display: EGLDisplay = EGL14.EGL_NO_DISPLAY
        private var context: EGLContext = EGL14.EGL_NO_CONTEXT
        private var surface: EGLSurface = EGL14.EGL_NO_SURFACE
        private var program = 0
        private var texture = 0
        private var vao = 0
        private var uSize = -1
        private var uAngle = -1
        private var uFrost = -1
        private var uEye = -1
        private var uAspect = -1
        private var uTex = -1
        private var uFlip = -1
        private var uHingeTop = -1

        private var sourceAspect = 1f
        private var hasSource = false
        private var lastUploaded: Bitmap? = null
        private var displayedTilt = 0.0
        private var lastParams: FrameParams? = null

        fun shutdown() {
            running = false
        }

        override fun run() {
            try {
                if (initEgl() && initGl()) {
                    Log.i(TAG, "渲染线程就绪")
                    loop()
                }
            } catch (t: Throwable) {
                Log.e(TAG, "渲染线程异常退出", t)
            } finally {
                releaseGl()
                releaseEgl()
            }
        }

        private fun initEgl(): Boolean {
            display = EGL14.eglGetDisplay(EGL14.EGL_DEFAULT_DISPLAY)
            if (display == EGL14.EGL_NO_DISPLAY) {
                Log.e(TAG, "eglGetDisplay 失败")
                return false
            }
            val version = IntArray(2)
            if (!EGL14.eglInitialize(display, version, 0, version, 1)) {
                Log.e(TAG, "eglInitialize 失败")
                return false
            }
            val configAttrs = intArrayOf(
                EGL14.EGL_RENDERABLE_TYPE, EGLExt.EGL_OPENGL_ES3_BIT_KHR,
                EGL14.EGL_RED_SIZE, 8,
                EGL14.EGL_GREEN_SIZE, 8,
                EGL14.EGL_BLUE_SIZE, 8,
                EGL14.EGL_ALPHA_SIZE, 8,
                EGL14.EGL_SURFACE_TYPE, EGL14.EGL_WINDOW_BIT,
                EGL14.EGL_NONE,
            )
            val configs = arrayOfNulls<EGLConfig>(1)
            val count = IntArray(1)
            if (!EGL14.eglChooseConfig(display, configAttrs, 0, configs, 0, 1, count, 0) || count[0] == 0) {
                Log.e(TAG, "没有可用的 ES3 配置")
                return false
            }
            val config = configs[0] ?: return false
            context = EGL14.eglCreateContext(
                display, config, EGL14.EGL_NO_CONTEXT,
                intArrayOf(EGL14.EGL_CONTEXT_CLIENT_VERSION, 3, EGL14.EGL_NONE), 0,
            )
            if (context == EGL14.EGL_NO_CONTEXT) {
                Log.e(TAG, "eglCreateContext 失败")
                return false
            }
            surface = EGL14.eglCreateWindowSurface(
                display, config, holder.surface, intArrayOf(EGL14.EGL_NONE), 0,
            )
            if (surface == EGL14.EGL_NO_SURFACE) {
                Log.e(TAG, "eglCreateWindowSurface 失败")
                return false
            }
            if (!EGL14.eglMakeCurrent(display, surface, surface, context)) {
                Log.e(TAG, "eglMakeCurrent 失败")
                return false
            }
            return true
        }

        private fun initGl(): Boolean {
            val vertexSource = readAsset("glass.vert") ?: return false
            val fragmentSource = readAsset("glass.frag") ?: return false
            val vertex = compile(GLES30.GL_VERTEX_SHADER, vertexSource) ?: return false
            val fragment = compile(GLES30.GL_FRAGMENT_SHADER, fragmentSource) ?: return false

            program = GLES30.glCreateProgram()
            GLES30.glAttachShader(program, vertex)
            GLES30.glAttachShader(program, fragment)
            GLES30.glLinkProgram(program)
            GLES30.glDeleteShader(vertex)
            GLES30.glDeleteShader(fragment)

            val linked = IntArray(1)
            GLES30.glGetProgramiv(program, GLES30.GL_LINK_STATUS, linked, 0)
            if (linked[0] == 0) {
                Log.e(TAG, "着色器链接失败: ${GLES30.glGetProgramInfoLog(program)}")
                return false
            }

            uSize = GLES30.glGetUniformLocation(program, "uSize")
            uAngle = GLES30.glGetUniformLocation(program, "uAngle")
            uFrost = GLES30.glGetUniformLocation(program, "uFrost")
            uEye = GLES30.glGetUniformLocation(program, "uEye")
            uAspect = GLES30.glGetUniformLocation(program, "uAspect")
            uTex = GLES30.glGetUniformLocation(program, "uTex")
            uFlip = GLES30.glGetUniformLocation(program, "uFlip")
            uHingeTop = GLES30.glGetUniformLocation(program, "uHingeTop")

            val vaos = IntArray(1)
            GLES30.glGenVertexArrays(1, vaos, 0)
            vao = vaos[0]

            val textures = IntArray(1)
            GLES30.glGenTextures(1, textures, 0)
            texture = textures[0]
            GLES30.glBindTexture(GLES30.GL_TEXTURE_2D, texture)
            GLES30.glTexParameteri(
                GLES30.GL_TEXTURE_2D, GLES30.GL_TEXTURE_MIN_FILTER, GLES30.GL_LINEAR_MIPMAP_LINEAR,
            )
            GLES30.glTexParameteri(GLES30.GL_TEXTURE_2D, GLES30.GL_TEXTURE_MAG_FILTER, GLES30.GL_LINEAR)
            GLES30.glTexParameteri(GLES30.GL_TEXTURE_2D, GLES30.GL_TEXTURE_WRAP_S, GLES30.GL_CLAMP_TO_EDGE)
            GLES30.glTexParameteri(GLES30.GL_TEXTURE_2D, GLES30.GL_TEXTURE_WRAP_T, GLES30.GL_CLAMP_TO_EDGE)
            return true
        }

        private fun readAsset(name: String): String? = try {
            appContext.assets.open(name).bufferedReader().use { it.readText() }
        } catch (t: Throwable) {
            Log.e(TAG, "读取 assets/$name 失败", t)
            null
        }

        private fun compile(type: Int, source: String): Int? {
            val shader = GLES30.glCreateShader(type)
            if (shader == 0) return null
            GLES30.glShaderSource(shader, source)
            GLES30.glCompileShader(shader)
            val compiled = IntArray(1)
            GLES30.glGetShaderiv(shader, GLES30.GL_COMPILE_STATUS, compiled, 0)
            if (compiled[0] == 0) {
                Log.e(TAG, "着色器编译失败: ${GLES30.glGetShaderInfoLog(shader)}")
                GLES30.glDeleteShader(shader)
                return null
            }
            return shader
        }

        private val width = IntArray(1)
        private val height = IntArray(1)

        private fun loop() {
            while (running) {
                EGL14.eglQuerySurface(display, surface, EGL14.EGL_WIDTH, width, 0)
                EGL14.eglQuerySurface(display, surface, EGL14.EGL_HEIGHT, height, 0)
                val w = width[0]
                val h = height[0]
                if (w <= 0 || h <= 0) {
                    sleep(FRAME_MS)
                    continue
                }

                val uploaded = uploadPendingBitmap()
                if (!hasSource) {
                    sleep(FRAME_MS)
                    continue
                }

                val params = snapshot()
                val paramsChanged = params != lastParams
                lastParams = params

                // Track the angle directly: the sensor already low-pass filters it,
                // and an extra smoothing stage made large changes visibly lag.
                displayedTilt = tiltDegrees(params)

                GLES30.glViewport(0, 0, w, h)
                GLES30.glUseProgram(program)
                GLES30.glBindVertexArray(vao)
                GLES30.glActiveTexture(GLES30.GL_TEXTURE0)
                GLES30.glBindTexture(GLES30.GL_TEXTURE_2D, texture)
                GLES30.glUniform1i(uTex, 0)
                GLES30.glUniform2f(uSize, w.toFloat(), h.toFloat())
                GLES30.glUniform1f(uAngle, displayedTilt.toFloat())
                GLES30.glUniform1f(uFrost, params.frost)
                GLES30.glUniform1f(uEye, params.eyeDistance)
                GLES30.glUniform1f(uAspect, sourceAspect)
                GLES30.glUniform1f(uFlip, if (params.flip180) 1f else 0f)
                GLES30.glUniform1f(uHingeTop, if (params.hingeAtTop) 1f else 0f)
                GLES30.glDrawArrays(GLES30.GL_TRIANGLES, 0, 3)
                GLES30.glBindVertexArray(0)

                if (!EGL14.eglSwapBuffers(display, surface)) {
                    Log.w(TAG, "eglSwapBuffers 失败，结束渲染线程")
                    return
                }

                val settled = !uploaded && !paramsChanged
                sleep(if (settled) IDLE_FRAME_MS else FRAME_MS)
            }
        }

        /**
         * 上传源图到纹理。
         *
         * 优先取显式投递的图；没有时回退到视图持有的那张，好让重建后的渲染线程
         * 自己把纹理补回来。同一张图只上传一次，所以稳态下每帧直接返回。
         *
         * @return 本次是否上传了新纹理。
         */
        private fun uploadPendingBitmap(): Boolean {
            val requested = pendingBitmap.getAndSet(null)
            val bitmap = requested ?: sourceBitmap.get() ?: return false
            if (bitmap === lastUploaded) return false
            GLES30.glBindTexture(GLES30.GL_TEXTURE_2D, texture)
            GLUtils.texImage2D(GLES30.GL_TEXTURE_2D, 0, bitmap, 0)
            GLES30.glGenerateMipmap(GLES30.GL_TEXTURE_2D)
            sourceAspect = bitmap.width.toFloat() / bitmap.height.toFloat()
            hasSource = true
            lastUploaded = bitmap
            Log.i(TAG, "源图已上传为纹理 ${bitmap.width}x${bitmap.height}")
            return true
        }

        private fun tiltDegrees(params: FrameParams): Double {
            val open = params.openAngleDeg
            if (open <= 1.0) return MAX_TILT_DEG
            val remaining = (1.0 - params.angleDeg / open).coerceIn(0.0, 1.0)
            return remaining * MAX_TILT_DEG
        }

        private fun releaseGl() {
            if (program != 0) GLES30.glDeleteProgram(program)
            if (texture != 0) GLES30.glDeleteTextures(1, intArrayOf(texture), 0)
            if (vao != 0) GLES30.glDeleteVertexArrays(1, intArrayOf(vao), 0)
            program = 0
            texture = 0
            vao = 0
        }

        private fun releaseEgl() {
            if (display != EGL14.EGL_NO_DISPLAY) {
                EGL14.eglMakeCurrent(
                    display, EGL14.EGL_NO_SURFACE, EGL14.EGL_NO_SURFACE, EGL14.EGL_NO_CONTEXT,
                )
                if (surface != EGL14.EGL_NO_SURFACE) EGL14.eglDestroySurface(display, surface)
                if (context != EGL14.EGL_NO_CONTEXT) EGL14.eglDestroyContext(display, context)
                EGL14.eglTerminate(display)
            }
            display = EGL14.EGL_NO_DISPLAY
            context = EGL14.EGL_NO_CONTEXT
            surface = EGL14.EGL_NO_SURFACE
        }
    }

    companion object {
        private const val TAG = "GlassSurfaceView"

        /** 玻璃效果相关的偏好都存这一个文件，两个页面共用。 */
        const val PREFS_NAME = "glass_effect"

        /** 玻璃页是否横屏。开关在主页，主页自身永远竖屏。 */
        const val KEY_LANDSCAPE = "landscape"

        const val DEFAULT_FROST = 0.09f

        /** 最大虚拟倾角：角度达到展开终点时效果归零。 */
        const val MAX_TILT_DEG = 80.0

        /**
         * 假定的人眼到屏幕距离（米）。
         *
         * 这个是效果强弱唯一的入口：调小 = 人离得更近 = 透视更强。0.45 m 比常见
         * 坐姿略近，用来换取更明显的透视。
         */
        const val VIEWING_DISTANCE_M = 0.45f

        /** 拿不到可信的物理 dpi 时用的屏幕高度（米），约合 6.2 英寸。 */
        private const val FALLBACK_SCREEN_HEIGHT_M = 0.157f

        /**
         * 眼距 = 观看距离 ÷ 屏幕物理高度。
         *
         * 换机型时最大的不确定性在物理 dpi：有的机型只有 `ydpi` 可信，有的只有 `xdpi`，
         * 还有两个都等于逻辑密度（完全不可用）。所以逐个试，取第一个能算出 6~30 cm
         * 屏高的；都不行才退回经验值。取值再钳到 1.5~6，避免异常 dpi 把透视毁掉。
         */
        fun deriveEyeDistance(
            context: Context,
            viewingDistanceM: Float = VIEWING_DISTANCE_M,
        ): Float {
            val metrics = context.resources.displayMetrics
            val heightMetres = listOf(metrics.ydpi, metrics.xdpi)
                .map { dpi ->
                    if (dpi > 1f && metrics.heightPixels > 0) {
                        metrics.heightPixels / dpi * 0.0254f
                    } else {
                        0f
                    }
                }
                .firstOrNull { it in 0.06f..0.30f }
                ?: FALLBACK_SCREEN_HEIGHT_M
            return (viewingDistanceM / heightMetres).coerceIn(1.5f, 6f)
        }

        private const val FRAME_MS = 16L
        private const val IDLE_FRAME_MS = 40L
    }
}
