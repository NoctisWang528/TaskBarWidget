#include "CardPch.h"
#include "CardMaterial.h"
#include "CardBackdropCapture.h"
#include "CardWindow.h"
#include "CardProtocol.h"
#include <winrt/Windows.Data.Json.h>
#include <iostream>
#include <limits>
#include <sstream>
#include <stdexcept>

namespace
{
    using namespace Cards;
    using namespace winrt::Windows::UI::Composition;

    void RequireMaterial(bool condition, const char* message)
    {
        if (!condition) throw std::runtime_error(message);
    }

    void RequireMaterialResult(bool condition, HRESULT result, const char* message)
    {
        if (condition && SUCCEEDED(result)) return;
        std::ostringstream text;
        text << message << " HRESULT=0x" << std::hex << static_cast<unsigned long>(result);
        throw std::runtime_error(text.str());
    }

    void CheckMaterialProtocol()
    {
        using namespace winrt::Windows::Data::Json;
        Snapshot snapshot;
        long long revision = 0;
        // Serialize a real JSON frame and pass it through the public wire parser.
        for (const auto* id : { L"Mica", L"LiquidGlass", L"Solid" })
        for (double blur : { 0., .35, 1. })
        {
            JsonObject config;
            config.Insert(L"material", JsonValue::CreateStringValue(id));
            config.Insert(L"glassBlur", JsonValue::CreateNumberValue(blur));
            config.Insert(L"revision", JsonValue::CreateNumberValue(static_cast<double>(++revision)));
            JsonObject payload; payload.Insert(L"quickCards", config);
            JsonObject frame;
            frame.Insert(L"version", JsonValue::CreateNumberValue(1));
            frame.Insert(L"type", JsonValue::CreateStringValue(L"command"));
            frame.Insert(L"action", JsonValue::CreateStringValue(L"cardSnapshot"));
            frame.Insert(L"payload", payload);
            const auto message = Protocol::Parse(winrt::to_string(frame.Stringify()));
            Protocol::Changes changes;
            RequireMaterial(message.hasPayload && Protocol::ApplySnapshot(snapshot, message.payloadJson, changes) &&
                changes.config && snapshot.config.revision == revision &&
                std::wstring(IdOf(snapshot.config.material.kind)) == id && snapshot.config.material.glassBlur == blur,
                "serialized material/blur/revision must reach the native snapshot");
            for (auto oldRevision : { revision, revision - 1 })
            {
                Protocol::Changes stale;
                const auto old = L"{\"quickCards\":{\"revision\":" + std::to_wstring(oldRevision) +
                    L",\"material\":\"unknown\",\"glassBlur\":-9}}";
                RequireMaterial(Protocol::ApplySnapshot(snapshot, old, stale) && !stale.config &&
                    snapshot.config.revision == revision && std::wstring(IdOf(snapshot.config.material.kind)) == id &&
                    snapshot.config.material.glassBlur == blur, "stale material must not replace newer settings");
            }
        }
        struct Case { const wchar_t* fields; MaterialKind kind; double blur; };
        for (const auto& item : {
            Case{ L"", MaterialKind::Mica, .35 }, // Legacy sender.
            Case{ L",\"material\":\"future-material\",\"glassBlur\":0.7", MaterialKind::Mica, .7 },
            Case{ L",\"material\":42,\"glassBlur\":\"bad\"", MaterialKind::Mica, .35 },
            Case{ L",\"material\":null,\"glassBlur\":null", MaterialKind::Mica, .35 },
            Case{ L",\"material\":\"LiquidGlass\",\"glassBlur\":-4", MaterialKind::LiquidGlass, 0 },
            Case{ L",\"material\":\"LiquidGlass\",\"glassBlur\":4", MaterialKind::LiquidGlass, 1 } })
        {
            Protocol::Changes changes;
            RequireMaterial(Protocol::ApplySnapshot(snapshot, L"{\"quickCards\":{\"revision\":" +
                std::to_wstring(++revision) + item.fields + L"}}", changes) && changes.config &&
                snapshot.config.material.kind == item.kind && snapshot.config.material.glassBlur == item.blur,
                "legacy/unknown/ill-typed material fields must use safe defaults and bounded blur");
        }
        for (double invalid : { std::numeric_limits<double>::quiet_NaN(),
            std::numeric_limits<double>::infinity(), -std::numeric_limits<double>::infinity() })
        {
            MaterialSettings settings;
            settings.Set(L"LiquidGlass", invalid);
            RequireMaterial(settings.kind == MaterialKind::LiquidGlass && settings.glassBlur == .35,
                "non-finite material blur must use the default");
        }
        Protocol::ResetRevisions(snapshot);
        Protocol::Changes restarted;
        RequireMaterial(Protocol::ApplySnapshot(snapshot,
            LR"({"quickCards":{"revision":0,"material":"LiquidGlass","glassBlur":1}})", restarted) &&
            restarted.config && snapshot.config.revision == 0 && snapshot.config.material.kind == MaterialKind::LiquidGlass &&
            snapshot.config.material.glassBlur == 1, "backend restart must accept material revision zero");
    }

    void CheckGlassGraph(Graphics& graphics, const Compositor& compositor, const CompositionGraphicsDevice& device)
    {
        // Deliberately bypass EffectsEnabled: disabled desktop transparency must
        // not hide an unsupported effect factory or failed mask construction.
        LiquidGlassBrush glass;
        ContainerVisual cachedVisual{ nullptr };
        CompositionBrush cachedBlur{ nullptr };
        for (bool dark : { false, true })
        for (float scale : { 1.f, 1.25f, 2.f })
        {
            const bool created = glass.Update(graphics, compositor, device, 384 * scale, 248 * scale, scale, .35, dark);
            if (!created) std::wcerr << L"Glass: " << glass.Diagnostic() << std::endl;
            RequireMaterialResult(created, glass.LastResult(), "create real glass factory and masks");
            const auto brush = glass.Brush();
            const auto rim = glass.Rim();
            const auto visual = glass.Visual();
            RequireMaterial(brush && rim && visual, "successful material must expose its visual, blur and rim");
            if (cachedVisual)
                RequireMaterial(visual == cachedVisual && brush == cachedBlur,
                    "theme/DPI changes must retain the material visual and base Gaussian");
            cachedVisual = visual; cachedBlur = brush;
            std::vector<SpriteVisual> layers;
            for (const auto& child : visual.Children()) layers.push_back(child.as<SpriteVisual>());
            RequireMaterial(layers.size() == 2 && layers.front().Brush() == brush &&
                layers.back().Brush().try_as<CompositionMaskBrush>(),
                "material must contain only the base blur and theme tint");
            const auto effect = brush.as<CompositionEffectBrush>();
            RequireMaterial(bool(effect.GetSourceParameter(L"Backdrop").try_as<CompositionBackdropBrush>()),
                "base Gaussian must sample a raw backdrop, never an effect brush");
            const auto mask = layers.back().Brush().as<CompositionMaskBrush>().Mask().as<CompositionNineGridBrush>();
            const auto surface = mask.Source().as<CompositionSurfaceBrush>();
            RequireMaterial(surface.Surface() != nullptr && surface.Stretch() == CompositionStretch::Fill,
                "theme mask must cover the card rather than show a centered source-size box");
            const auto rimSource = rim.as<CompositionNineGridBrush>().Source().as<CompositionSurfaceBrush>();
            RequireMaterial(rimSource.Surface() != nullptr && rimSource.Stretch() == CompositionStretch::Fill,
                "rim must stretch its real surface across the full card");
            for (double blur : { 0., 1., .35, 0., 1. })
            {
                const bool updated = glass.Update(graphics, compositor, device, 416 * scale, 310 * scale, scale, blur, dark);
                RequireMaterialResult(updated, glass.LastResult(), "repeat/resize glass graph");
                RequireMaterial(glass.Visual() == visual && glass.Brush() == brush && glass.Rim() == rim,
                    "blur and size updates must reuse the graph brush and DPI/theme masks");
                int layerIndex = 0;
                for (const auto& child : visual.Children())
                {
                    RequireMaterial(layerIndex < static_cast<int>(layers.size()) && child == layers[layerIndex],
                        "resize/blur must retain material layer identities and order");
                    ++layerIndex;
                }
                RequireMaterial(layerIndex == static_cast<int>(layers.size()), "material layers must not disappear on update");
                float amount = -1;
                RequireMaterial(effect.Properties().TryGetScalar(L"Blur.Amount", amount) == CompositionGetValueStatus::Succeeded &&
                    std::abs(amount - static_cast<float>(24 * blur * scale)) < .001f,
                    "blur endpoints must update the actual effect property");
            }
        }
        glass.Release();
        RequireMaterial(!glass.Visual() && !glass.Brush() && !glass.Rim(), "release must detach glass resources");
        const bool recreated = glass.Update(graphics, compositor, device, 384, 248, 1, .35, true);
        RequireMaterialResult(recreated, glass.LastResult(), "recreate released graph");
    }

    void CheckGlassRefraction(Graphics& graphics, const Compositor& compositor, const CompositionGraphicsDevice& device)
    {
        constexpr int width = 384;
        constexpr int height = 248;
        std::vector<uint8_t> sourcePixels(static_cast<size_t>(width) * height * 4);
        for (int y = 0; y < height; ++y)
            for (int x = 0; x < width; ++x)
            {
                const bool bright = ((x / 16) + (y / 16)) % 2 == 0;
                const uint8_t value = bright ? 240 : 20;
                const auto index = (static_cast<size_t>(y) * width + x) * 4;
                sourcePixels[index] = value;
                sourcePixels[index + 1] = value;
                sourcePixels[index + 2] = value;
                sourcePixels[index + 3] = 255;
            }

        auto* context = graphics.ResourceContext();
        Microsoft::WRL::ComPtr<ID2D1Bitmap1> source;
        HRESULT hr = context->CreateBitmap(
            D2D1::SizeU(width, height), sourcePixels.data(), width * 4,
            D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_NONE,
                D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED), 96, 96),
            source.GetAddressOf());
        RequireMaterialResult(SUCCEEDED(hr), hr, "create monochrome checkerboard backdrop");

        LiquidGlassBrush glass;
        RequireMaterialResult(glass.Update(graphics, compositor, device, width, height, 1.f, 0., true),
            glass.LastResult(), "prepare glass for live refraction check");
        RequireMaterial(!glass.LiveActive() && glass.RenderedFrames() == 0,
            "fresh glass must report fallback until a desktop frame renders");

        BackdropFrame frame;
        frame.bitmap = source;
        frame.screenBounds = { 0, 0, width, height };
        frame.serial = 1;
        RequireMaterialResult(glass.RenderBackdrop(graphics, compositor, device, frame, frame.screenBounds),
            glass.LastResult(), "render live rounded-rect refraction and spectral dispersion");
        RequireMaterial(glass.LiveActive() && glass.RenderedFrames() == 1 && glass.Scene() != nullptr &&
            glass.Diagnostic() == L"live-refraction",
            "live refraction pass must record active state, frame counter and scene bitmap");

        Microsoft::WRL::ComPtr<ID2D1Bitmap1> readback;
        hr = context->CreateBitmap(
            D2D1::SizeU(width, height), nullptr, 0,
            D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_CPU_READ | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
                D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED), 96, 96),
            readback.GetAddressOf());
        RequireMaterialResult(SUCCEEDED(hr), hr, "create refraction readback bitmap");
        const D2D1_POINT_2U origin{};
        const D2D1_RECT_U copyRect{ 0, 0, static_cast<UINT32>(width), static_cast<UINT32>(height) };
        hr = readback->CopyFromBitmap(&origin, glass.Scene(), &copyRect);
        RequireMaterialResult(SUCCEEDED(hr), hr, "copy refracted scene bitmap for readback");
        D2D1_MAPPED_RECT mapped{};
        hr = readback->Map(D2D1_MAP_OPTIONS_READ, &mapped);
        RequireMaterialResult(SUCCEEDED(hr), hr, "map refracted scene bitmap");

        int maxBevelDispersion = 0;
        int maxInteriorChroma = 0;
        int refractedShiftedPixels = 0;
        for (int y = 2; y < height - 2; ++y)
            for (int x = 2; x < width - 2; ++x)
            {
                const uint8_t* p = mapped.bits + static_cast<size_t>(y) * mapped.pitch + static_cast<size_t>(x) * 4;
                const int b = p[0], g = p[1], r = p[2];
                const int chroma = (std::max)(std::abs(r - b), (std::max)(std::abs(r - g), std::abs(g - b)));
                const bool inInterior = x >= 60 && x < width - 60 && y >= 60 && y < height - 60;
                const bool inBevel = x < 24 || x >= width - 24 || y < 24 || y >= height - 24;
                if (inInterior) maxInteriorChroma = (std::max)(maxInteriorChroma, chroma);
                if (inBevel)
                {
                    maxBevelDispersion = (std::max)(maxBevelDispersion, chroma);
                    const auto srcIndex = (static_cast<size_t>(y) * width + x) * 4;
                    if (std::abs(g - static_cast<int>(sourcePixels[srcIndex + 1])) > 80) ++refractedShiftedPixels;
                }
            }
        readback->Unmap();

        RequireMaterial(refractedShiftedPixels > 400,
            "bevel lens must noticeably refract and bend high-contrast backdrop stripes inward");
        RequireMaterial(maxBevelDispersion >= 20 && maxInteriorChroma <= 2,
            "chromatic dispersion must split R/G/B along refracted contrast edges while keeping interior neutral");

        glass.ClearBackdrop();
        RequireMaterial(!glass.LiveActive() && glass.Scene() == nullptr,
            "ClearBackdrop must return status to non-live fallback");
        frame.serial = 2;
        RequireMaterialResult(glass.RenderBackdrop(graphics, compositor, device, frame, frame.screenBounds),
            glass.LastResult(), "re-render live refraction after fallback");
        RequireMaterial(glass.LiveActive() && glass.RenderedFrames() == 2,
            "re-entering live refraction must increment rendered frame counter");
    }

    struct MaterialServices : CardHostServices
    {
        void OnRequest(const std::wstring&, const std::wstring&) override {}
        void OnHideCards(const std::wstring&) override {}
        void OnOpenSettings() override {}
        void OnLayoutInvalidated() override {}
        void OnGraphicsFailure(HRESULT result) override { RequireMaterialResult(false, result, "card graphics failure"); }
    };

    void CheckMaterialCards(CardEnvironment environment, const Snapshot& snapshot)
    {
        environment.reducedMotion = true;
        auto fixture = snapshot;
        fixture.media.isPlaying = false;
        fixture.lyrics.isPlaying = false;
        fixture.weather.alerts.clear();
        AudioEndpoint endpoint;
        endpoint.id = L"material-test-output"; endpoint.name = L"Test output";
        endpoint.isDefault = true; endpoint.volume = .5;
        fixture.audio.outputs = { endpoint };
        PeripheralDevice peripheral;
        peripheral.id = L"material-test-device"; peripheral.name = L"Test device";
        peripheral.showInCard = true; peripheral.canRead = true;
        peripheral.connection = L"connected";
        PeripheralBatteryReading battery; battery.percentage = 75;
        peripheral.batteries.push_back(battery);
        fixture.peripheralBattery.revision = 1;
        fixture.peripheralBattery.devices = { peripheral };
        MaterialServices services;
        for (auto kind : { Kind::Weather, Kind::Traffic, Kind::Device, Kind::Media, Kind::Update, Kind::PeripheralBattery })
        {
            std::wcout << L"Material transitions: " << IdOf(kind) << std::endl;
            CardWindow card(kind, services);
            RequireMaterial(card.Create(environment, GetModuleHandleW(nullptr)), "create material card");
            card.SetSnapshot(fixture);
            const auto window = card.Handle();
            for (bool dark : { false, true })
            for (float scale : { 1.f, 1.25f, 2.f })
            {
                environment.dark = dark;
                const RECT bounds{ -30000, -30000, -30000 + static_cast<LONG>(Layout::WidthDip(kind) * scale),
                    -30000 + static_cast<LONG>(Layout::DefaultHeightDip(kind) * scale) };
                card.Place(bounds, scale);
                card.Show(false);
                const auto content = card.ContentBrush();
                const auto regions = card.Regions().All();
                struct Appearance { const wchar_t* material; double blur; bool highContrast; };
                for (const auto& appearance : {
                    Appearance{L"LiquidGlass", 0, false}, Appearance{L"LiquidGlass", 1, false},
                    Appearance{L"LiquidGlass", .35, true}, Appearance{L"Mica", .35, false},
                    Appearance{L"Solid", .35, false}, Appearance{L"LiquidGlass", .35, false} })
                {
                    const bool highContrast = appearance.highContrast;
                    environment.highContrast = highContrast;
                    environment.material.Set(appearance.material, appearance.blur);
                    card.ApplyEnvironment(environment);
                    RequireMaterialResult(!FAILED(card.MaterialResult()), card.MaterialResult(), "card material transition");
                    const bool expectedGlass = environment.material.kind == MaterialKind::LiquidGlass &&
                        !highContrast && LiquidGlassBrush::EffectsEnabled();
                    RequireMaterial(card.HasLiquidGlass() == expectedGlass, "glass must follow material/accessibility policy");
                    RECT actual{};
                    const auto stored = card.Bounds();
                    RequireMaterial(card.Handle() == window && GetWindowRect(window, &actual) &&
                        EqualRect(&actual, &bounds) && EqualRect(&stored, &bounds) && card.DpiScale() == scale,
                        "material switch must preserve HWND and pixel geometry");
                    RequireMaterial(card.HasRenderedContent() && card.ContentBrush() == content,
                        "material switch must retain the independently rendered content brush");
                    const auto& current = card.Regions().All();
                    RequireMaterial(current.size() == regions.size(), "material must preserve input region count");
                    for (size_t i = 0; i < regions.size(); ++i)
                    {
                        const auto& a = regions[i]; const auto& b = current[i];
                        RequireMaterial(a.id == b.id && a.action == b.action && a.enabled == b.enabled &&
                            EqualRect(&a.bounds, &b.bounds), "material must preserve input identities and bounds");
                    }
                    const auto input = std::find_if(current.begin(), current.end(), [](const auto& region) {
                        return region.enabled && region.type == Layout::RegionType::Button; });
                    if (input != current.end())
                    {
                        const auto region = *input;
                        POINT point{ bounds.left + static_cast<LONG>((region.bounds.left + region.bounds.right) * scale / 2),
                            bounds.top + static_cast<LONG>((region.bounds.top + region.bounds.bottom) * scale / 2) };
                        card.OnPointer(WM_LBUTTONDOWN, point, true);
                        RequireMaterial(card.CurrentInteraction().pressed == region.id,
                            "scaled pointer must still press the original control after a material switch");
                        card.CancelPointer(); // Exercise input without firing external commands.
                        card.OnMouseLeave();
                    }
                }
                const auto paints = card.ContentPaintCount();
                const auto builds = card.RegionBuildCount();
                for (int repeat = 0; repeat < 8; ++repeat) card.Place(bounds, scale);
                RequireMaterial(card.ContentPaintCount() == paints && card.RegionBuildCount() == builds &&
                    card.ContentBrush() == content, "unchanged glass placement must not redraw or rebuild content");
                auto resized = bounds; resized.right += 20;
                card.Place(resized, scale);
                RequireMaterial(card.HasRenderedContent() && card.ContentBrush() == content,
                    "glass resize must resize content surfaces without replacing their brush");
                card.Place(bounds, scale);
            }
            card.CancelPointer(); card.OnMouseLeave();
            const auto paints = card.ContentPaintCount();
            int timers = 0;
            const auto deadline = GetTickCount64() + 120;
            while (GetTickCount64() < deadline)
            {
                MSG message{};
                while (GetTickCount64() < deadline && PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
                {
                    if (message.hwnd == window && message.message == WM_TIMER) ++timers;
                    TranslateMessage(&message); DispatchMessageW(&message);
                }
                Sleep(5);
            }
            RequireMaterial(timers == 0 && card.ContentPaintCount() == paints,
                "idle glass must not introduce a resident redraw timer");
            card.Hide(false);
        }
    }
}

void TestCardMaterials(Cards::CardEnvironment environment, const Cards::Snapshot& snapshot)
{
    try
    {
        std::cout << "Material protocol and graph checks" << std::endl;
        CheckMaterialProtocol();
        CheckGlassGraph(*environment.graphics, environment.compositor, environment.device);
        CheckGlassRefraction(*environment.graphics, environment.compositor, environment.device);
        CheckMaterialCards(environment, snapshot);
        std::cout << "Material wire defaults/revisions, real glass graph reuse, card transitions and idle timer checks passed.\n";
    }
    catch (const winrt::hresult_error& error)
    {
        std::cerr << "Material probe HRESULT=0x" << std::hex << error.code().value << std::dec << ": " <<
            winrt::to_string(error.message()) << '\n';
        throw;
    }
}
