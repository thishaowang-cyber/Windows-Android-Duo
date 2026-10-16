package com.example.laptophinge

import android.content.Context
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import android.os.Handler
import android.os.HandlerThread
import com.example.laptophinge.sensor.AngleFilter
import com.example.laptophinge.sensor.HingeAngleCalculator
import com.example.laptophinge.sensor.LockStateMachine
import com.example.laptophinge.net.AngleServer
import kotlin.math.abs
import kotlin.math.acos
import kotlin.math.sqrt

/**
 * 传感器中枢：用**重力水平仪**直接解出笔记本开合角。
 *
 * ## 原理
 *
 * 手机平贴在笔记本 A 面上时，手机屏幕法线就等于盖板法线。而盖板相对**水平面**的
 * 倾角，就是屏幕法线与竖直方向的夹角——这只取决于重力在设备 z 轴上的分量：
 *
 *     h = acos(−a_z / |a|)        // 0 = 合盖，90 = 竖直，180 = 摊平
 *
 * 这个式子**不需要知道铰链轴的方向**：绕屏幕法线怎么旋转（手机横贴、竖贴、
 * 贴歪了）都不改变法线本身，因此不必再测那根轴。
 *
 * 相比原来的「四元数相对姿态 + 陀螺仪积分 + 互补融合」：
 *
 *  - 不需要参考姿态，不需要开合一次求轴；
 *  - 不是积分量，因此**永不漂移**，也不存在「停下来被拉回原位」；
 *  - 没有滤波滞后以外的时间常数，跟手。
 *
 * ## 两个必须知道的代价
 *
 * 1. **它测的是盖板相对水平面的倾角，不是相对底座的夹角。** 笔记本电脑放在倾斜的
 *    表面（腿、床上、支架）时，两者会相差底座那一个倾角，而水平仪无从得知这个差。
 *    放平在桌面上时两者相等。
 * 2. **越接近完全合盖，读数越抖。** 因为 h → 0 时 cos(h) 趋于平坦，a_z 对角度的
 *    变化不敏感，反解时噪声被放大（`dh/da_z = 1/(|a|·sin h)`）。竖直附近最准，
 *    合盖附近最抖——这正好是效果最强的那一段。`observability = |sin h|` 反映这一点，
 *    界面据此提示。若此处抖动不可接受，正道是「水平仪做绝对值 + 陀螺仪做短期平滑」，
 *    但那又会把漂移带回来。
 *
 * ## 线程模型
 *
 * 传感器回调跑在专属的 [HandlerThread] 上，[Listener] 回调**总是 post 回主线程**，
 * 因此 UI 层无需自己做线程切换。
 */
class HingeSensorService(
    context: Context,
    private val listener: Listener,
) : SensorEventListener {

    interface Listener {
        /** 每次解算完成（主线程）。 */
        fun onSample(sample: SensorSample, result: AngleResult)

        /** 铰链轴估计更新（主线程）。水平仪方案不使用，保留接口以免调用方改签名。 */
        fun onAxisUpdated(axis: DoubleArray, confidence: Double)

        /** 传感器注册状态变化，用于 UI 提示（主线程）。 */
        fun onSensorStatus(status: SensorStatus)
    }

    /** 传感器可用性快照，直接反映在 UI 上——现场排障全靠它。 */
    data class SensorStatus(
        val hasAccelerometer: Boolean,
        val hasGyroscope: Boolean,
        /** 实际用于测倾角的传感器名称，null 表示没有可用的重力源。 */
        val sourceName: String?,
        /** true = 用系统融合的 TYPE_GRAVITY；false = 退回原始加速度计。 */
        val usingSystemGravity: Boolean,
        val gyroMaxHz: Float,
        val sampleRateHz: Float,
    )

    /** 一次解算的完整输出，UI 只依赖这个结构。 */
    data class AngleResult(
        /** 滤波后的开合角，单位度。 */
        val filteredAngleDeg: Double,
        /** 水平仪直接读出的原始角（未滤波），单位度。 */
        val accelAngleDeg: Double,
        /** 未使用：水平仪方案不需要陀螺仪。恒为 0，保留字段以兼容现有 UI。 */
        val gyroAngleDeg: Double,
        /** 映射后的物理开合角（0=合盖，90=垂直，180=平摊）。未标定时等于原始角。 */
        val physicalAngleDeg: Double,
        /** 是否已建立物理角度映射。 */
        val mapped: Boolean,
        /** 未使用：水平仪方案没有铰链轴。恒为零向量。 */
        val axis: DoubleArray,
        /** 未使用：恒为 0。 */
        val axisConfidence: Double,
        /** 未使用：恒为 false。 */
        val axisCalibrated: Boolean,
        /**
         * 该角度处的可信度 0~1 = `|sin h|`。
         * 接近合盖或完全摊平时数学上不可观测，读数会被噪声放大。
         */
        val observability: Double,
        /** 当前是否判定为静止。 */
        val stationary: Boolean,
        val locked: Boolean,
        /** 未滤波的原始角，用于标定参考点。 */
        val rawAngleDeg: Double,
        val timestampNanos: Long,
    ) {
        override fun equals(other: Any?): Boolean {
            if (this === other) return true
            if (other !is AngleResult) return false
            return filteredAngleDeg == other.filteredAngleDeg &&
                accelAngleDeg == other.accelAngleDeg &&
                gyroAngleDeg == other.gyroAngleDeg &&
                physicalAngleDeg == other.physicalAngleDeg &&
                mapped == other.mapped &&
                axis.contentEquals(other.axis) &&
                axisConfidence == other.axisConfidence &&
                axisCalibrated == other.axisCalibrated &&
                observability == other.observability &&
                stationary == other.stationary &&
                locked == other.locked &&
                rawAngleDeg == other.rawAngleDeg &&
                timestampNanos == other.timestampNanos
        }

        override fun hashCode(): Int {
            var r = filteredAngleDeg.hashCode()
            r = 31 * r + accelAngleDeg.hashCode()
            r = 31 * r + gyroAngleDeg.hashCode()
            r = 31 * r + physicalAngleDeg.hashCode()
            r = 31 * r + mapped.hashCode()
            r = 31 * r + axis.contentHashCode()
            r = 31 * r + axisConfidence.hashCode()
            r = 31 * r + axisCalibrated.hashCode()
            r = 31 * r + observability.hashCode()
            r = 31 * r + stationary.hashCode()
            r = 31 * r + locked.hashCode()
            r = 31 * r + rawAngleDeg.hashCode()
            r = 31 * r + timestampNanos.hashCode()
            return r
        }
    }

    // ---------------------------------------------------------------- 传感器

    private val sensorManager = context.getSystemService(Context.SENSOR_SERVICE) as SensorManager

    /**
     * 优先用系统的 [Sensor.TYPE_GRAVITY]：它由系统自己融合加速度计与陀螺仪得到，
     * 已经去掉了线性加速度的污染，且省掉我们自己再做一层低通。
     * 没有该传感器时退回原始加速度计（静止时两者等价，运动时会差一些）。
     */
    private val gravitySensor: Sensor? = sensorManager.getDefaultSensor(Sensor.TYPE_GRAVITY)
    private val accelSensor: Sensor? = sensorManager.getDefaultSensor(Sensor.TYPE_ACCELEROMETER)
    private val gyroSensor: Sensor? = sensorManager.getDefaultSensor(Sensor.TYPE_GYROSCOPE)

    private val tiltSensor: Sensor? = gravitySensor ?: accelSensor
    private val usingSystemGravity: Boolean = gravitySensor != null

    // ---------------------------------------------------------------- 数学模块

    /** 只用来持有两点映射（原始角 → 物理角）。水平仪本身不需要轴与参考姿态。 */
    private val calculator = HingeAngleCalculator()

    private val angleFilter = AngleFilter(
        mode = AngleFilter.Mode.ONE_EURO,
        // 水平仪读数在竖直附近很干净，但接近合盖时会被放大，所以静止时保留一定滤波。
        minCutoffHz = 2.0,
        beta = 0.12,
    )

    private val lockMachine = LockStateMachine()

    /** 静止判定阈值（度/秒），取滤波器的速度估计。 */
    private val stillRateDegPerSec = 2.0

    // ---------------------------------------------------------------- 运行状态

    private val thread = HandlerThread("hinge-sensor").apply { start() }
    private val handler = Handler(thread.looper)
    private val mainHandler = Handler(android.os.Looper.getMainLooper())

    private var registered = false
    private var running = false

    /** 重力向量（设备系，屏幕朝上时 z 为正）。 */
    private val gravity = FloatArray(3)
    private var lastTimestampNanos = 0L
    private var lastRawAngle = 0.0

    @Volatile private var logWriter: SensorLogWriter? = null

    @Volatile private var angleServer: AngleServer? = null

    private var lastStatus: SensorStatus? = null

    // ---------------------------------------------------------------- 对外控制

    val status: SensorStatus
        get() = SensorStatus(
            hasAccelerometer = accelSensor != null,
            hasGyroscope = gyroSensor != null,
            sourceName = tiltSensor?.name,
            usingSystemGravity = usingSystemGravity,
            gyroMaxHz = gyroSensor?.let { if (it.minDelay > 0) 1_000_000f / it.minDelay else 0f } ?: 0f,
            sampleRateHz = SAMPLE_RATE_HZ,
        )

    val isRunning: Boolean get() = running

    val isLogging: Boolean get() = logWriter != null

    /** 当前是否已建立物理角度映射（已标定 0° 与 90° 两个参考点）。 */
    val isMapped: Boolean get() = calculator.currentMapping?.isValid == true

    /** 开始采集。重复调用无副作用。 */
    fun start() {
        if (registered) return
        val rateUs = (1_000_000f / SAMPLE_RATE_HZ).toInt()
        tiltSensor?.let {
            sensorManager.registerListener(this, it, rateUs, rateUs, handler)
        }
        registered = true
        running = true
        emitStatus(force = true)
    }

    /** 停止采集并关闭日志。 */
    fun stop() {
        if (!registered) return
        sensorManager.unregisterListener(this)
        registered = false
        running = false
    }

    fun release() {
        stop()
        stopLogging()
        stopSending()
        thread.quitSafely()
    }

    /**
     * 把滤波器重新对齐到当前读数。
     *
     * 水平仪读的是绝对角度，没有「参考姿态」可以重定，所以这里只清掉滤波器的历史，
     * 让显示立刻跳到当前真实值。物理映射不受影响。
     */
    fun rezero() {
        handler.post {
            angleFilter.reset(lastRawAngle)
            lockMachine.reset()
        }
    }

    /** 水平仪方案没有铰链轴可测，保留空实现以免调用方改签名。 */
    fun resetAxis() = Unit

    /**
     * 记录一个物理角度参考点，用于建立原始角 → 物理角的线性映射。
     *
     * 水平仪读出的角已经是绝对角，理论上不需要标定；保留两点映射是为了吸收
     * 「手机没贴平」「笔记本底座本身不水平」这类固定偏差。
     *
     * @param physicalDeg 0 = 完全合盖；90 = 屏幕垂直
     * @param rawAngleDeg 该时刻的原始角（由 UI 从 [AngleResult.rawAngleDeg] 传入）
     */
    fun captureMappingPoint(physicalDeg: Double, rawAngleDeg: Double) {
        handler.post { calculator.captureMappingPoint(physicalDeg, rawAngleDeg) }
    }

    // ---------------------------------------------------------------- 日志

    /**
     * 开始记录 CSV。
     *
     * 写入 `getExternalFilesDir()`，属于应用专属目录，无需任何运行时权限。
     */
    fun startLogging(context: Context, label: String = "") {
        if (logWriter != null) return
        val w = SensorLogWriter.create(context, label) ?: return
        logWriter = w
    }

    /** 停止记录并返回文件绝对路径。 */
    fun stopLogging(): String? {
        val w = logWriter ?: return null
        logWriter = null
        w.close()
        return w.path
    }

    // ---------------------------------------------------------------- 局域网发送

    /** 当前是否在把角度送往电脑。 */
    val isSending: Boolean get() = angleServer != null

    /** 手机在局域网里的地址（电脑端要填的那个），未开启时返回 null。 */
    val shareAddress: String? get() = angleServer?.address

    /** 电脑连接用的端口。 */
    val sharePort: Int get() = AngleServer.PORT

    /**
     * 开始把开合角通过局域网送到电脑。
     *
     * 手机开 TCP 服务端、电脑主动连过来：Windows 防火墙默认拦入站、放行出站，
     * 这样电脑端**不需要任何防火墙规则**。细节见 [AngleServer]。重复调用无副作用。
     *
     * @param onStatus 状态文本回调，已切回主线程。
     */
    fun startSending(onStatus: (String) -> Unit) {
        if (angleServer != null) return
        val server = AngleServer { text -> mainHandler.post { onStatus(text) } }
        server.start()
        angleServer = server
    }

    fun stopSending() {
        angleServer?.stop()
        angleServer = null
    }

    // ---------------------------------------------------------------- 传感器回调

    override fun onSensorChanged(event: SensorEvent) {
        if (event.sensor.type != tiltSensor?.type) return
        System.arraycopy(event.values, 0, gravity, 0, 3)
        processLevel(event.timestamp)
    }

    override fun onAccuracyChanged(sensor: Sensor?, accuracy: Int) {
        if (sensor?.type == tiltSensor?.type) emitStatus(force = true)
    }

    // ---------------------------------------------------------------- 解算主流程

    private fun processLevel(timestampNanos: Long) {
        val magnitude = sqrt(
            gravity[0].toDouble() * gravity[0] +
                gravity[1].toDouble() * gravity[1] +
                gravity[2].toDouble() * gravity[2],
        )
        // 自由落体或传感器异常时模长会失控；此时读数没有意义，直接丢弃这一帧。
        if (magnitude < 1e-3 || !magnitude.isFinite()) return

        // h = acos(−a_z / |a|)：屏幕朝上时 a_z = +g，得 180°（摊平）；
        // 屏幕朝下（合盖）时 a_z = −g，得 0°。
        val cosFromVertical = (-gravity[2] / magnitude).coerceIn(-1.0, 1.0)
        val raw = Math.toDegrees(acos(cosFromVertical))

        // 可观测度 = |sin h| = 重力在屏幕平面内的分量占比。接近合盖/摊平时趋于 0。
        val observability = sqrt(
            (1.0 - cosFromVertical * cosFromVertical).coerceIn(0.0, 1.0),
        )

        lastRawAngle = raw
        lastTimestampNanos = timestampNanos

        val filtered = angleFilter.update(raw, timestampNanos)
        val mapping = calculator.currentMapping?.takeIf { it.isValid }
        val physical = mapping?.toPhysicalDegrees(filtered) ?: filtered

        val stationary = abs(angleFilter.velocityDegPerSec) < stillRateDegPerSec
        lockMachine.update(physical, timestampNanos / 1_000_000L)

        val result = AngleResult(
            filteredAngleDeg = filtered,
            accelAngleDeg = raw,
            gyroAngleDeg = 0.0,
            physicalAngleDeg = physical,
            mapped = isMapped,
            axis = DoubleArray(3),
            axisConfidence = 0.0,
            axisCalibrated = false,
            observability = observability,
            stationary = stationary,
            locked = lockMachine.isLocked,
            rawAngleDeg = raw,
            timestampNanos = timestampNanos,
        )

        val sample = SensorSample(
            accel = gravity.copyOf(),
            gyro = FloatArray(3),
            rotationVector = null,
            rotationAccuracy = 0,
            timestampNanos = timestampNanos,
        )

        logWriter?.append(sample, result)
        angleServer?.submit(physical)
        mainHandler.post { listener.onSample(sample, result) }
    }

    // ---------------------------------------------------------------- 状态上报

    private fun emitStatus(force: Boolean) {
        val s = status
        if (!force && s == lastStatus) return
        lastStatus = s
        mainHandler.post { listener.onSensorStatus(s) }
    }

    companion object {
        /**
         * 采样率 100 Hz。
         *
         * 重力传感器是系统合成的低带宽信号，本身不需要高采样率；
         * 100 Hz 只是为了让显示刷新得足够顺，且不超过 Android 12+ 的免权限上限。
         */
        const val SAMPLE_RATE_HZ = 100f
    }
}
