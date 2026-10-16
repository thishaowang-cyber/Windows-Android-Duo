package com.example.laptophinge

import com.example.laptophinge.sensor.Vec3

/**
 * 一次传感器事件的快照。
 *
 * 所有时间戳统一使用 `SystemClock.elapsedRealtimeNanos()` 时基（传感器事件自带），
 * 不要用 `System.currentTimeMillis()`：后者会被系统校时（NTP）跳变，
 * 一旦回跳，速度估计和积分都会算出负数。
 */
data class SensorSample(
    /** 三轴线性加速度，含重力，单位 m/s²。 */
    val accel: FloatArray,
    /** 三轴角速度，单位 rad/s。 */
    val gyro: FloatArray,
    /** 四元数 (x, y, z, w)；未注册或无数据时为 null。 */
    val rotationVector: FloatArray?,
    /** 旋转向量传感器的精度等级，见 SensorManager.SENSOR_STATUS_*。 */
    val rotationAccuracy: Int,
    /** 事件时间戳（纳秒）。 */
    val timestampNanos: Long,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is SensorSample) return false
        return accel.contentEquals(other.accel) &&
            gyro.contentEquals(other.gyro) &&
            (rotationVector?.contentEquals(other.rotationVector ?: FloatArray(0))
                ?: (other.rotationVector == null)) &&
            rotationAccuracy == other.rotationAccuracy &&
            timestampNanos == other.timestampNanos
    }

    override fun hashCode(): Int {
        var result = accel.contentHashCode()
        result = 31 * result + gyro.contentHashCode()
        result = 31 * result + (rotationVector?.contentHashCode() ?: 0)
        result = 31 * result + rotationAccuracy
        result = 31 * result + timestampNanos.hashCode()
        return result
    }

    /** 重力方向（设备系单位向量）。加速度计在静止时约等于 −重力方向。 */
    fun gravityDirection(): DoubleArray? {
        val g = Vec3.of(-accel[0].toDouble(), -accel[1].toDouble(), -accel[2].toDouble())
        return Vec3.normalize(g)
    }

    /** 加速度模长，用于判断「是否在做线性加速运动」。 */
    fun accelMagnitude(): Double =
        kotlin.math.sqrt(
            accel[0].toDouble() * accel[0] + accel[1].toDouble() * accel[1] + accel[2].toDouble() * accel[2],
        )

    /** 陀螺仪模长 (rad/s)。 */
    fun gyroMagnitude(): Double = Vec3.norm(Vec3.of(gyro[0].toDouble(), gyro[1].toDouble(), gyro[2].toDouble()))
}
