package com.example.laptophinge

import android.content.Context
import android.os.Environment
import java.io.BufferedWriter
import java.io.File
import java.io.FileWriter
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 传感器数据 CSV 记录器。
 *
 * ## 为什么必须带原始数据
 *
 * 只记「算出来的角度」是没用的：滤错了、轴估计错了、零偏漂了，事后都无法复现。
 * 这里把三路原始读数（加速度计 / 陀螺仪 / 四元数）+ 三路解算结果全部落盘，
 * 于是电脑端 Python 可以离线重算任意算法变体（改滤波系数、换互补策略），
 * 也能做 Allan 方差分析陀螺仪零偏稳定性——判断这台手机精度上限的唯一方法。
 *
 * ## 写入策略
 *
 * 100 Hz 下每秒 100 行。用 [BufferedWriter] 并在每 [FLUSH_INTERVAL_ROWS] 行 flush 一次，
 * 断电/杀进程最多丢失一小段。CSV 表头自带单位，Python 端可直接 `pd.read_csv`。
 *
 * 文件写入 `Android/data/<包名>/files/logs/`，属于应用专属目录，**不需要任何运行时权限**，
 * 也可以在文件管理器 / `adb pull` 里直接取走。
 */
class SensorLogWriter private constructor(
    val path: String,
    private val writer: BufferedWriter,
) {
    private var rows = 0
    private var closed = false

    /** 追加一行。线程安全由调用方保证（本项目只在传感器线程调用）。 */
    fun append(sample: SensorSample, result: HingeSensorService.AngleResult) {
        if (closed) return
        val sb = StringBuilder(192)
        sb.append(sample.timestampNanos).append(',')
        sb.append(result.timestampNanos).append(',')

        sample.accel.forEach { sb.append(fmt(it)).append(',') }
        sample.gyro.forEach { sb.append(fmt(it)).append(',') }

        val rv = sample.rotationVector
        if (rv != null) {
            rv.forEach { sb.append(fmt(it)).append(',') }
        } else {
            sb.append(",,,,")
        }

        sb.append(sample.rotationAccuracy).append(',')
        sb.append(fmt(result.filteredAngleDeg)).append(',')
        sb.append(fmt(result.accelAngleDeg)).append(',')
        sb.append(fmt(result.gyroAngleDeg)).append(',')
        sb.append(fmt(result.physicalAngleDeg)).append(',')
        sb.append(fmt(result.rawAngleDeg)).append(',')
        sb.append(fmt(result.axis[0])).append(',')
        sb.append(fmt(result.axis[1])).append(',')
        sb.append(fmt(result.axis[2])).append(',')
        sb.append(fmt(result.axisConfidence)).append(',')
        sb.append(fmt(result.observability)).append(',')
        sb.append(if (result.stationary) '1' else '0').append(',')
        sb.append(if (result.locked) '1' else '0').append(',')
        sb.append(if (result.mapped) '1' else '0')

        try {
            writer.write(sb.toString())
            writer.newLine()
            rows++
            if (rows % FLUSH_INTERVAL_ROWS == 0) writer.flush()
        } catch (_: Exception) {
            // 写盘失败不应该让传感器线程崩掉——角度显示比日志重要
            closed = true
        }
    }

    fun close() {
        if (closed) return
        closed = true
        try {
            writer.flush()
            writer.close()
        } catch (_: Exception) {
            // 忽略关闭异常
        }
    }

    companion object {
        private const val FLUSH_INTERVAL_ROWS = 100

        /**
         * CSV 表头。列名自带单位，避免事后猜。
         * `t_ns` 为传感器事件时间戳（elapsedRealtimeNanos 时基），跨设备可比较。
         */
        private val HEADER = listOf(
            "t_ns", "t_result_ns",
            "ax", "ay", "az",
            "gx", "gy", "gz",
            "qx", "qy", "qz", "qw",
            "rot_accuracy",
            "angle_fused_deg", "angle_accel_deg", "angle_gyro_deg",
            "angle_physical_deg", "angle_raw_deg",
            "axis_x", "axis_y", "axis_z",
            "axis_confidence", "observability",
            "stationary", "locked", "mapped",
        ).joinToString(",")

        private fun fmt(v: Double): String = String.format(Locale.US, "%.6f", v)

        private fun fmt(v: Float): String = String.format(Locale.US, "%.6f", v)

        /**
         * 创建日志文件。`label` 会进入文件名，便于区分「静止测试 / 开合测试 / 发热测试」。
         *
         * @return 创建失败返回 null（例如存储不可用），此时 App 继续工作，只是不记录
         */
        fun create(context: Context, label: String = ""): SensorLogWriter? {
            return try {
                val dir = File(context.getExternalFilesDir(Environment.DIRECTORY_DOCUMENTS), "logs")
                if (!dir.exists() && !dir.mkdirs()) return null

                val stamp = SimpleDateFormat("yyyyMMdd_HHmmss", Locale.US).format(Date())
                val safeLabel = label.trim().replace(Regex("[^A-Za-z0-9_\\-\\u4e00-\\u9fa5]"), "_")
                val name = if (safeLabel.isEmpty()) "sensors_$stamp.csv" else "sensors_${safeLabel}_$stamp.csv"
                val file = File(dir, name)

                val w = BufferedWriter(FileWriter(file, false), 1 shl 16)
                w.write(HEADER)
                w.newLine()
                SensorLogWriter(file.absolutePath, w)
            } catch (_: Exception) {
                null
            }
        }
    }
}
