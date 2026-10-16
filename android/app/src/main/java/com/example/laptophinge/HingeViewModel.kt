package com.example.laptophinge

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue

/**
 * 全部 UI 状态。
 *
 * 做成不可变快照：传感器 100 Hz 推送，若每个事件都重建整棵状态树会明显发热掉电，
 * 因此 [HingeViewModel] 只在**显示值真正变化**时才提交新快照（角度按 0.1° 量化）。
 *
 * [trace] 刻意不参与 `equals`：它是绘制细节，比较它会让每次采样都触发重组。
 */
data class HingeUiState(
    val status: HingeSensorService.SensorStatus? = null,
    val angleDeg: Double = 0.0,
    val accelAngleDeg: Double = 0.0,
    val gyroAngleDeg: Double = 0.0,
    val rawAngleDeg: Double = 0.0,
    val mapped: Boolean = false,
    val axis: DoubleArray = DoubleArray(3),
    val axisConfidence: Double = 0.0,
    val observability: Double = 0.0,
    val stationary: Boolean = false,
    val locked: Boolean = false,
    val running: Boolean = false,
    val logging: Boolean = false,
    val lastLogPath: String? = null,
    val sampleCount: Long = 0,
    val message: String? = null,
    /** 最近的滤波后角度序列，用于画迷你曲线。 */
    val trace: List<Float> = emptyList(),
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is HingeUiState) return false
        return status == other.status &&
            angleDeg == other.angleDeg &&
            accelAngleDeg == other.accelAngleDeg &&
            gyroAngleDeg == other.gyroAngleDeg &&
            rawAngleDeg == other.rawAngleDeg &&
            mapped == other.mapped &&
            axis.contentEquals(other.axis) &&
            axisConfidence == other.axisConfidence &&
            observability == other.observability &&
            stationary == other.stationary &&
            locked == other.locked &&
            running == other.running &&
            logging == other.logging &&
            lastLogPath == other.lastLogPath &&
            sampleCount == other.sampleCount &&
            message == other.message
    }

    override fun hashCode(): Int {
        var r = status?.hashCode() ?: 0
        r = 31 * r + angleDeg.hashCode()
        r = 31 * r + sampleCount.hashCode()
        r = 31 * r + running.hashCode()
        r = 31 * r + mapped.hashCode()
        return r
    }

    /** 开合状态的中文描述，UI 与日志共用。角度本身是绝对的，不依赖映射。 */
    val phaseText: String
        get() = when {
            angleDeg <= 45.0 -> "已合盖"
            angleDeg >= 90.0 -> "已打开"
            else -> "半开"
        }
}

/**
 * 传感器回调 → Compose 状态。
 *
 * 职责有三：转发 [HingeSensorService.Listener] 回调、抽稀高频更新、维护迷你曲线缓冲。
 * [HingeSensorService] 已保证所有回调都在主线程，因此这里不需要额外同步。
 */
class HingeViewModel {

    var state by mutableStateOf(HingeUiState())
        private set

    /** 由 Activity 在 `setContent` 之前赋值。 */
    var onUiState: ((HingeUiState) -> Unit)? = null

    private val inner = object : HingeSensorService.Listener {
        override fun onSample(sample: SensorSample, result: HingeSensorService.AngleResult) {
            this@HingeViewModel.onSample(sample, result)
        }

        override fun onAxisUpdated(axis: DoubleArray, confidence: Double) {
            lastAxis = axis
            lastAxisConfidence = confidence
            message = "铰链轴已估计：置信度 ${(confidence * 100).toInt()}%"
            submit(force = true)
        }

        override fun onSensorStatus(status: HingeSensorService.SensorStatus) {
            lastStatus = status
            submit(force = true)
        }
    }

    /** 供 [MainActivity] 构造服务时传入。 */
    val listener: HingeSensorService.Listener get() = inner

    // ------------------------------------------------------------ 内部状态

    private var lastStatus: HingeSensorService.SensorStatus? = null
    private var lastAxis: DoubleArray? = null
    private var lastAxisConfidence = 0.0
    private var pendingResult: HingeSensorService.AngleResult? = null

    private var sampleCount = 0L
    private var running = false
    private var logging = false
    private var lastLogPath: String? = null
    private var message: String? = null

    private var lastSubmittedAngle = Double.NaN
    private val trace = ArrayList<Float>(TRACE_LENGTH)

    // ------------------------------------------------------------ 回调入口

    fun onSample(sample: SensorSample, result: HingeSensorService.AngleResult) {
        pendingResult = result
        sampleCount++

        if (sampleCount % TRACE_DECIMATION == 0L) {
            trace.add(result.physicalAngleDeg.toFloat())
            while (trace.size > TRACE_LENGTH) trace.removeAt(0)
        }

        submit(force = false)
    }

    fun onRunningChanged(value: Boolean) {
        running = value
        submit(force = true)
    }

    fun onLoggingChanged(value: Boolean, path: String?) {
        logging = value
        if (path != null) lastLogPath = path
        submit(force = true)
    }

    fun onMessage(text: String?) {
        message = text
        submit(force = true)
    }

    /** 清空曲线缓冲，例如重新标定后。 */
    fun clearTrace() {
        trace.clear()
        lastSubmittedAngle = Double.NaN
        submit(force = true)
    }

    /**
     * 提交新快照。抽稀逻辑在这里：角度按 0.1° 量化后相同就跳过，
     * 除非 [force]（状态位/消息变化时必须提交）。
     */
    private fun submit(force: Boolean) {
        val result = pendingResult
        val quantized = result?.let { Math.round(it.physicalAngleDeg * 10.0) / 10.0 } ?: 0.0

        if (!force && quantized == lastSubmittedAngle) return
        lastSubmittedAngle = quantized

        val next = HingeUiState(
            status = lastStatus,
            angleDeg = quantized,
            accelAngleDeg = result?.accelAngleDeg ?: 0.0,
            gyroAngleDeg = result?.gyroAngleDeg ?: 0.0,
            rawAngleDeg = result?.rawAngleDeg ?: 0.0,
            mapped = result?.mapped ?: false,
            axis = lastAxis ?: result?.axis ?: DoubleArray(3),
            axisConfidence = lastAxisConfidence.takeIf { it > 0.0 } ?: (result?.axisConfidence ?: 0.0),
            observability = result?.observability ?: 0.0,
            stationary = result?.stationary ?: false,
            locked = result?.locked ?: false,
            running = running,
            logging = logging,
            lastLogPath = lastLogPath,
            sampleCount = sampleCount,
            message = message,
            trace = ArrayList(trace),
        )
        state = next
        onUiState?.invoke(next)
    }

    companion object {
        const val TRACE_LENGTH = 240
        const val TRACE_DECIMATION = 10L
    }
}
