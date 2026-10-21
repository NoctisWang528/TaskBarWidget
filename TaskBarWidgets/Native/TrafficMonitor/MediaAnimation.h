#pragma once

#include "MediaFrameContract.h"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <string>
#include <vector>

namespace NativeMedia
{
    inline double SmootherStep(double value)
    {
        const double t = Clamp01(value);
        return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
    }

    class WordScanSmoother
    {
    public:
        void Reset()
        {
            m_hasValue = false;
            m_target.reset();
            m_identity.clear();
            m_lineIndex = -1;
            m_from = 0;
            m_to = 0;
            m_started = 0;
            m_duration = 0;
            m_frozen.reset();
            m_frozenIdentity.clear();
            m_frozenLine = -1;
            m_wasPlaying = false;
        }

        void Observe(const std::wstring& trackId, int lineIndex, bool isPlaying,
            std::optional<double> progress, std::uint64_t now)
        {
            if (!progress)
            {
                Reset();
                m_wasPlaying = isPlaying;
                return;
            }

            const double target = Clamp01(*progress);
            const bool identityChanged = trackId != m_identity || lineIndex != m_lineIndex;
            if (identityChanged)
            {
                m_identity = trackId;
                m_lineIndex = lineIndex;
                m_frozen.reset();
                m_frozenIdentity.clear();
                m_frozenLine = -1;
                SetImmediate(target, now);
                m_wasPlaying = isPlaying;
                return;
            }

            // Observing the same authoritative target again must not restart
            // an in-flight interpolation. The renderer calls Observe only when
            // the frame's target changes, but keep this guard for other callers.
            if (m_target && *m_target == target && m_wasPlaying == isPlaying)
                return;

            if (!isPlaying)
            {
                if (!m_frozen || m_wasPlaying)
                {
                    const double current = Current(now).value_or(m_to);
                    m_frozen = current;
                    m_frozenIdentity = trackId;
                    m_frozenLine = lineIndex;
                }
                m_target = target;
                m_to = m_frozen.value_or(target);
                m_from = m_to;
                m_started = now;
                m_duration = 0;
                m_hasValue = true;
                m_wasPlaying = isPlaying;
                return;
            }

            if (!m_wasPlaying && m_frozen && m_frozenIdentity == trackId &&
                m_frozenLine == lineIndex && target > *m_frozen)
            {
                m_from = *m_frozen;
                m_to = target;
                m_started = now;
                m_duration = 180;
                m_target = target;
                m_hasValue = true;
                m_frozen.reset();
                m_wasPlaying = isPlaying;
                return;
            }
            m_frozen.reset();

            const double current = Current(now).value_or(m_to);
            if (target < current - 0.02 || target - current > 0.35 ||
                (m_target && target < *m_target - 0.02))
            {
                SetImmediate(target, now);
            }
            else
            {
                m_from = current;
                m_to = target;
                m_started = now;
                m_duration = 90;
                m_target = target;
                m_hasValue = true;
            }
            m_wasPlaying = isPlaying;
        }

        std::optional<double> Current(std::uint64_t now) const
        {
            if (!m_hasValue) return std::nullopt;
            if (m_duration == 0 || now <= m_started) return m_to;
            if (now >= m_started + m_duration) return m_to;
            const double elapsed = static_cast<double>(now - m_started);
            const double duration = static_cast<double>(m_duration);
            const double t = elapsed / duration;
            if (m_duration == 180) return Clamp01(m_to - (m_to - m_from) * (1.0 - SmootherStep(t)));
            return Clamp01(m_from + (m_to - m_from) * t);
        }

        bool Active(std::uint64_t now) const
        {
            return m_hasValue && m_duration > 0 && now < m_started + m_duration;
        }

        bool HasValue() const { return m_hasValue; }

    private:
        void SetImmediate(double value, std::uint64_t now)
        {
            m_hasValue = true;
            m_from = m_to = value;
            m_target = value;
            m_started = now;
            m_duration = 0;
        }

        bool m_hasValue{};
        std::optional<double> m_target;
        std::wstring m_identity;
        int m_lineIndex{ -1 };
        double m_from{};
        double m_to{};
        std::uint64_t m_started{};
        std::uint64_t m_duration{};
        std::optional<double> m_frozen;
        std::wstring m_frozenIdentity;
        int m_frozenLine{ -1 };
        bool m_wasPlaying{};
    };

    class SpectrumAnimator
    {
    public:
        void Reset()
        {
            m_targets.clear();
            m_values.clear();
        }

        void Configure(const SpectrumTuning& tuning, const std::vector<double>& values, bool isPlaying)
        {
            auto normalized = tuning;
            normalized.Normalize();
            m_tuning = normalized;
            SetTargets(values, isPlaying);
        }

        void SetTargets(const std::vector<double>& values, bool isPlaying)
        {
            const int count = (std::max)(1, m_tuning.barCount);
            if (static_cast<int>(m_targets.size()) != count)
            {
                m_targets.assign(count, 0.0);
                m_values.assign(count, 0.0);
            }
            if (!isPlaying || values.empty())
            {
                std::fill(m_targets.begin(), m_targets.end(), 0.0);
                return;
            }
            for (int i = 0; i < count; ++i)
            {
                const size_t start = static_cast<size_t>(i) * values.size() / count;
                const size_t end = (std::min)(values.size(),
                    (std::max)(start + 1, static_cast<size_t>(i + 1) * values.size() / count));
                double peak = 0;
                for (size_t index = start; index < end; ++index)
                    peak = (std::max)(peak, Clamp01(values[index]));
                m_targets[i] = peak;
            }
        }

        bool Tick(double elapsedSeconds)
        {
            const double frames = std::clamp(elapsedSeconds * 60.0, 0.5, 2.4);
            bool settled = true;
            for (size_t i = 0; i < m_targets.size(); ++i)
            {
                const double target = m_targets[i];
                const double current = m_values[i];
                const double base = target > current ? m_tuning.rise : m_tuning.fall;
                const double rate = 1.0 - std::pow(1.0 - base, frames);
                double next = current + (target - current) * rate;
                if (std::abs(next - target) < 0.002) next = target;
                m_values[i] = next;
                if (std::abs(next - target) >= 0.002) settled = false;
            }
            return !settled;
        }

        void SettleNow()
        {
            m_values = m_targets;
        }

        const std::vector<double>& Values() const { return m_values; }
        const SpectrumTuning& Tuning() const { return m_tuning; }

        float HeightPixels(size_t index, double layoutScale) const
        {
            if (index >= m_values.size()) return 1.0f;
            const double value = (m_tuning.minHeight + m_values[index] * m_tuning.heightRange) *
                (std::max)(0.2, layoutScale);
            return static_cast<float>((std::max)(1.0, value));
        }

    private:
        SpectrumTuning m_tuning;
        std::vector<double> m_targets;
        std::vector<double> m_values;
    };

    struct RollTransition
    {
        TransitionKind kind{ TransitionKind::Immediate };
        Frame from;
        Frame to;
        std::uint64_t started{};
        int durationMs{};
        bool active{};

        void Begin(TransitionKind transition, const Frame& previous, const Frame& target,
            std::uint64_t now, int milliseconds)
        {
            kind = transition;
            from = previous;
            to = target;
            started = now;
            durationMs = milliseconds;
            active = transition != TransitionKind::Immediate && milliseconds > 0;
        }

        double Progress(std::uint64_t now) const
        {
            if (!active || durationMs <= 0) return 1.0;
            return Clamp01(static_cast<double>(now - started) / durationMs);
        }

        bool Finished(std::uint64_t now) const
        {
            return !active || now >= started + static_cast<std::uint64_t>((std::max)(0, durationMs));
        }

        void Reset()
        {
            active = false;
            kind = TransitionKind::Immediate;
            durationMs = 0;
        }
    };
}
