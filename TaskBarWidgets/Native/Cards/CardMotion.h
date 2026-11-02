#pragma once
#include <Windows.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <winrt/Windows.UI.Composition.h>

namespace Cards
{
    inline constexpr UINT MotionCompleted = WM_APP + 0x216;

    // Card/menu opacity and translation are compositor animations. The clock
    // estimates hit testing and duration; it never submits per-frame values.
    class CardMotion
    {
    public:
        ~CardMotion() { Detach(); }

        void Bind(winrt::Windows::UI::Composition::Visual const& value, bool shown, float scale)
        {
            Detach();
            visual = value;
            Set(shown, scale);
        }

        void Detach()
        {
            Cancel();
            visual = nullptr;
        }

        void Set(bool shown, float scale)
        {
            Cancel();
            from = to = shown ? 1.0f : 0.0f;
            if (visual)
            {
                visual.Opacity(to);
                visual.Offset({0, OffsetDip() * scale, 0});
            }
        }

        void Start(bool shown, float scale, bool animate, HWND window, int milliseconds)
        {
            const float target = shown ? 1.0f : 0.0f;
            if (animate && running && target == to) return;
            const float current = Opacity();
            if (!animate || !visual || (!running && std::abs(target - current) < 0.001f))
            {
                Set(shown, scale);
                return;
            }
            Cancel(false);
            from = current;
            to = target;
            animationScale = scale;
            duration = std::max(1, static_cast<int>(std::lround(milliseconds * std::abs(to - from))));
            started = std::chrono::steady_clock::now();
            running = true;
            try
            {
                const auto compositor = visual.Compositor();
                // x(t)=t and y(t)=1-(1-t)^3: the same restrained cubic ease-out
                // used by hit testing, without a 16-ms window timer.
                const auto easing = compositor.CreateCubicBezierEasingFunction(
                    {1.0f / 3.0f, 1.0f}, {2.0f / 3.0f, 1.0f});
                const auto opacity = compositor.CreateScalarKeyFrameAnimation();
                opacity.Duration(std::chrono::milliseconds(duration));
                // Replacing an animation takes its actual compositor value,
                // including when the UI thread/commit was delayed.
                opacity.InsertExpressionKeyFrame(0, L"this.StartingValue");
                opacity.InsertKeyFrame(1, to, easing);
                const auto offset = compositor.CreateVector3KeyFrameAnimation();
                offset.Duration(std::chrono::milliseconds(duration));
                offset.InsertExpressionKeyFrame(0, L"this.StartingValue");
                offset.InsertKeyFrame(1, {0, -6.0f * (1 - to) * scale, 0}, easing);
                batch = compositor.CreateScopedBatch(winrt::Windows::UI::Composition::CompositionBatchTypes::Animation);
                const auto token = generation;
                completed = batch.Completed([window, token](auto const&, auto const&)
                {
                    // No captured card pointer; a destroyed/reused HWND or an
                    // interrupted animation cannot complete a newer transition.
                    PostMessageW(window, MotionCompleted, token, 0);
                });
                visual.StartAnimation(L"Opacity", opacity);
                visual.StartAnimation(L"Offset", offset);
                batch.End();
            }
            catch (winrt::hresult_error const&) { Set(shown, scale); }
        }

        bool Complete(UINT_PTR token)
        {
            if (!running || token != generation) return false;
            running = false;
            from = to;
            if (batch) { batch.Completed(completed); batch = nullptr; }
            visual.Opacity(to);
            visual.Offset({0, -6.0f * (1 - to) * animationScale, 0});
            visual.StopAnimation(L"Opacity");
            visual.StopAnimation(L"Offset");
            return true;
        }

        bool Running() const { return running; }
        float Opacity() const
        {
            if (!running) return to;
            const float elapsed = std::chrono::duration<float, std::milli>(
                std::chrono::steady_clock::now() - started).count();
            const float remaining = 1 - std::clamp(elapsed / duration, 0.0f, 1.0f);
            return from + (to - from) * (1 - remaining * remaining * remaining);
        }
        float OffsetDip() const { return -6.0f * (1 - Opacity()); }

    private:
        void Cancel(bool stop = true)
        {
            generation = ++nextGeneration;
            running = false;
            try
            {
                if (batch) { batch.Completed(completed); batch = nullptr; }
                if (visual && stop) { visual.StopAnimation(L"Opacity"); visual.StopAnimation(L"Offset"); }
            }
            catch (winrt::hresult_error const&) { batch = nullptr; }
        }

        inline static std::atomic<UINT_PTR> nextGeneration{};
        UINT_PTR generation{};
        winrt::Windows::UI::Composition::Visual visual{nullptr};
        winrt::Windows::UI::Composition::CompositionScopedBatch batch{nullptr};
        winrt::event_token completed{};
        std::chrono::steady_clock::time_point started{};
        float from{}, to{};
        float animationScale{1.0f};
        int duration{1};
        bool running{};
    };
}
