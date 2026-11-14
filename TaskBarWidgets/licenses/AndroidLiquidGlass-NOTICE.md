# AndroidLiquidGlass / Backdrop

- Source: https://github.com/Kyant0/AndroidLiquidGlass
- Revision: `eb4a0ea2e17542ea28c2e71088f7dd9acf77aad5` (`kmp` branch).
- Copyright 2025 Kyant. Licensed under Apache License 2.0; the unmodified license is in `AndroidLiquidGlass-LICENSE`.
- Reference files: `backdrop/src/commonMain/kotlin/com/kyant/backdrop/internal/Shaders.kt`, `effects/Lens.kt`, `effects/Blur.kt`, and `highlight/HighlightStyle.kt`.
- Modified/adapted implementations: `Native/Cards/CardMaterial.cpp` and `CardRefraction.cpp`. Rounded-rectangle distance/normal, the circular lens displacement profile and directional edge illumination are adapted to C++ and Direct2D. The Android AGSL runtime, Compose library, examples and artwork are not bundled.
- Platform adaptation: Desktop Duplication, with Windows Graphics Capture as a compatibility fallback, supplies live, private background textures. Private Direct2D HLSL lens refraction with seven spectral samples, colour-matrix saturation and blur render completed surfaces for Composition; capture exclusion prevents self-feedback. The former Composition backdrop affine workaround remains removed. A single untransformed Composition blur is the fallback. Capsule controls adapt the interaction proportions of LiquidSlider, LiquidToggle and LiquidBottomTabs, with smaller slider heads and displacement of private background/rail pixels. No AGSL shader runtime or control artwork is bundled. No affiliation with Apple or upstream is implied.

The reference checkout is separate from the product. Building and distributing TaskBarWidgets does not require that checkout.
