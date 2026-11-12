// Included by Updater.cpp after its transaction primitives. No separate installer or network stack.
// The legacy-compatible envelope temporarily uses this updater as TaskBarWidgets.exe.
constexpr const char *BridgeFormat = "TBW_BRIDGE_V1";
constexpr const char *BridgeDirectory = "Media/UpgradePayload/";

bool BridgePending(const Job &j)
{
    auto path = j.work / L"bridge-pending.json";
    Safe(path);
    if (!fs::exists(FileApiPath(path)))
        return false;
    auto d = Parse(path, 4096);
    auto v = yyjson_doc_get_root(d.get());
    Require(String(v, "format") == BridgeFormat && String(v, "nonce") == j.nonce &&
        String(v, "fileManifestSha256") == j.manifestHash, "Bridge does not belong to this job");
    return true;
}

// The final inventory still uses the exact normal program-path policy. Blobs are
// transport only: they cannot introduce personal paths or arbitrary destinations.
Job PrepareBridgeStage(const Job &outer)
{
    const auto envelope = Manifest(outer.stage / L"app-files.json", outer.version);
    VerifyStage(outer, envelope);
    auto d = Parse(Child(outer.stage, std::string(BridgeDirectory) + "bridge.json"), 4096);
    auto v = yyjson_doc_get_root(d.get());
    Require(String(v, "format") == BridgeFormat && String(v, "version") == outer.version,
        "Unsupported bridge envelope");
    auto manifest = Child(outer.stage, std::string(BridgeDirectory) + "manifest.json");
    Job next = outer;
    next.manifestHash = LowerA(String(v, "fileManifestSha256"));
    Require(HashString(next.manifestHash) && Sha(manifest) == next.manifestHash, "Bridge inventory hash mismatch");
    auto files = Manifest(manifest, outer.version);
    next.stage = outer.stage.parent_path() / L"bridge-stage";
    Safe(next.stage);
    Require(!Equal(next.stage, outer.stage) && !Under(next.stage, outer.install) &&
        !Under(outer.install, next.stage) && !Equal(next.stage, outer.work) &&
        !Under(next.stage, outer.work) && !Under(outer.work, next.stage), "Unsafe bridge staging directory");
    std::map<std::string, File> sources;
    for (const auto &file : envelope)
        sources.emplace(file.path, file);
    Require(sources.count("TaskBarWidgets.exe") && sources.count("TaskBarWidgets.Updater.exe") &&
        sources.at("TaskBarWidgets.exe").hash == sources.at("TaskBarWidgets.Updater.exe").hash,
        "Bridge entry must be the packaged updater");
    fs::create_directories(FileApiPath(next.stage));
    for (const auto &file : files)
    {
        Require(LowerA(file.path).rfind("media/upgradepayload/", 0) != 0, "Nested bridge payload is forbidden");
        auto source = sources.find(file.path);
        if (source == sources.end() || source->second.hash != file.hash || source->second.bytes != file.bytes)
            source = sources.find(std::string(BridgeDirectory) + file.hash.substr(0, 16) + ".bin");
        Require(source != sources.end() && source->second.hash == file.hash && source->second.bytes == file.bytes,
            "Final file is not bound to the bridge inventory");
        Replace(Child(outer.stage, source->first, true), Child(next.stage, file.path, true), file.hash);
    }
    Replace(manifest, next.stage / L"app-files.json", next.manifestHash);
    VerifyStage(next, files);
    return next;
}

void BridgeJobLocation(const Job &j)
{
#ifndef TBW_UPDATER_TEST
    PWSTR local = nullptr;
    Require(SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local)), "Update cache unavailable");
    fs::path cache(local);
    CoTaskMemFree(local);
    auto root = j.install.wstring();
    int size = LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE, root.data(), (int)root.size(),
        nullptr, 0, nullptr, nullptr, 0);
    Require(size > 0, "Cannot normalize installation");
    std::wstring upper(size, L'\0');
    Require(LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE, root.data(), (int)root.size(),
        upper.data(), size, nullptr, nullptr, 0) == size, "Cannot normalize installation");
    auto bytes = Utf8(upper);
    UCHAR hash[32]{};
    Require(BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0, (PUCHAR)bytes.data(), (ULONG)bytes.size(), hash, 32) >= 0,
        "Cannot identify installation cache");
    std::wstring key;
    for (auto byte : hash)
    {
        key += L"0123456789abcdef"[byte >> 4];
        key += L"0123456789abcdef"[byte & 15];
    }
    Require(j.file.filename() == L"job.json" && Equal(j.work.parent_path(), cache / L"TaskBarWidgets/Updates" / key / L"jobs"),
        "Bridge job is outside this installation's cache");
#else
    (void)j;
#endif
}

int BridgeEntry(int count, LPWSTR *argv)
{
    fs::path work;
    try
    {
        wchar_t image[32768]{};
        auto length = GetModuleFileNameW(nullptr, image, 32768);
        Require(length && length < 32768 && fs::path(image).filename() == L"TaskBarWidgets.exe",
            "This is an update helper, not the product entry point");
        fs::path jobPath;
        std::string nonce;
        if (count == 4)
        {
            jobPath = Absolute(Utf8(argv[2]));
            nonce = Utf8(argv[3]);
        }
        else
        {
            auto d = Parse(fs::path(image).parent_path() / L"Media/UpgradePayload/resume.json", 4096);
            auto v = yyjson_doc_get_root(d.get());
            jobPath = Absolute(String(v, "jobPath"));
            nonce = String(v, "nonce");
        }
        auto j = LoadJob(jobPath, true);
        BridgeJobLocation(j);
        Require(nonce == j.nonce, "Bridge nonce mismatch");
        auto approval = Parse(j.work / L"shutdown-approved.json", 4096);
        Require(String(yyjson_doc_get_root(approval.get()), "nonce") == j.nonce, "Bridge requires the original shutdown approval");
        work = j.work;
        Handle oldHelper;
        if (count == 4)
        {
            auto status = Parse(j.work / L"status.json", 4096);
            auto v = yyjson_doc_get_root(status.get());
            auto pid = Number(v, "helperPid");
            Require(pid > 0 && pid <= MAXDWORD && String(v, "phase") == "starting", "Bridge requires the original running updater");
            oldHelper.h = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, (DWORD)pid);
            Require(oldHelper.h && WaitForSingleObject(oldHelper.h, 0) == WAIT_TIMEOUT &&
                Equal(Image(oldHelper.h), j.work / L"TaskBarWidgets.Updater.exe"), "Original updater identity mismatch");
            // Launch and its journal write are separate operations in legacy helpers.
            auto until = GetTickCount64() + 2000;
            while (ReadJournal(j).launched != GetCurrentProcessId() && GetTickCount64() < until)
                Sleep(20);
            auto journal = ReadJournal(j);
            Require(journal.phase == "starting" && journal.launched == GetCurrentProcessId(), "Bridge launch was not journaled");
            Require(Sha(j.install / L"app-files.json") == LowerA(j.manifestHash) &&
                Sha(image) == Sha(j.stage / L"TaskBarWidgets.exe"), "Installed bridge differs from the confirmed package");
            auto next = PrepareBridgeStage(j);
            Dependencies(next);
            // A crash between the legacy helper's completion and replacement of
            // its EXE must still have a recovery command that understands bridges.
            Replace(j.stage / L"TaskBarWidgets.Updater.exe", j.work / L"BridgeRecovery.exe",
                Sha(image));
            Atomic(j.work / L"recover.cmd", "@echo off\r\n\"%~dp0BridgeRecovery.exe\" --recover \"%~dp0job.json\"\r\n");
            Atomic(j.work / L"bridge-pending.json", "{\"format\":" + Quote(BridgeFormat) + ",\"nonce\":" + Quote(j.nonce) +
                ",\"fileManifestSha256\":" + Quote(j.manifestHash) + "}");
            Atomic(j.install / L"Media/UpgradePayload/resume.json", "{\"jobPath\":" + Quote(Utf8(j.file.wstring())) +
                ",\"nonce\":" + Quote(j.nonce) + "}");
            // This acknowledges a verified bridge, NOT final product health. The
            // next helper keeps this journal/backup until native + UI health passes.
            Atomic(j.work / L"health.json", "{\"ready\":true,\"nonce\":" + Quote(j.nonce) +
                ",\"version\":" + Quote(j.version) + "}");
            Require(WaitForSingleObject(oldHelper.h, 30000) == WAIT_OBJECT_0, "Original updater did not finish bridge handoff");
#ifdef TBW_UPDATER_TEST
            if (fs::exists(FileApiPath(j.work / L"test-bridge-handoff-stop")))
                return 0;
#endif
        }
        Require(BridgePending(j), "No confirmed bridge continuation is available");
        auto journal = ReadJournal(j);
        Require(!(journal.bridge && journal.phase == "complete") && journal.phase != "rolledBack", "Bridge transaction is already finished");
        // Never overwrite a live legacy helper, including on manual retry.
        auto status = Parse(j.work / L"status.json", 4096);
        auto pid = Number(yyjson_doc_get_root(status.get()), "helperPid");
        Require(pid > 0 && pid <= MAXDWORD, "Invalid updater identity");
        Handle previous(OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, (DWORD)pid));
        Require(!previous.h || WaitForSingleObject(previous.h, 0) == WAIT_OBJECT_0 ||
            !Equal(Image(previous.h), j.work / L"TaskBarWidgets.Updater.exe"), "An updater is still running");
        auto envelope = Manifest(j.stage / L"app-files.json", j.version);
        VerifyStage(j, envelope);
        const auto updater = std::find_if(envelope.begin(), envelope.end(), [](auto &f) { return f.path == "TaskBarWidgets.Updater.exe"; });
        Require(updater != envelope.end(), "Bridge updater is missing");
        Replace(j.stage / L"TaskBarWidgets.Updater.exe", j.work / L"TaskBarWidgets.Updater.exe", updater->hash);
        const bool resume = !journal.bridge && journal.phase == "complete";
        std::wstring command = L"\"" + (j.work / L"TaskBarWidgets.Updater.exe").wstring() +
            (resume ? L"\" --continue-bridge \"" : L"\" --recover \"") + j.file.wstring() + L"\"";
        if (resume)
            command += L" " + std::to_wstring(GetCurrentProcessId());
        STARTUPINFOW si{sizeof(si)};
        PROCESS_INFORMATION pi{};
        Require(CreateProcessW((j.work / L"TaskBarWidgets.Updater.exe").c_str(), command.data(), nullptr, nullptr, FALSE,
            0, nullptr, j.work.c_str(), &si, &pi), "Cannot start bridge continuation; run the recovery entry");
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return 0; // Continuation waits for this exact bridge process to exit normally.
    }
    catch (const std::exception &e)
    {
        if (!work.empty())
            try { Atomic(work / L"bridge-error.json", "{\"message\":" + Quote(e.what()) + "}"); } catch (...) {}
#ifndef TBW_UPDATER_TEST
        if (count == 1)
            MessageBoxW(nullptr, L"升级未完成。请运行更新作业目录中的 recover.cmd；手动安装请下载 portable.zip 完整包。",
                L"TaskBarWidgets", MB_OK | MB_ICONWARNING);
#endif
        return 1;
    }
}

int ContinueBridge(Job j, DWORD bridgePid)
{
    BridgeJobLocation(j);
    Require(BridgePending(j), "Missing confirmed bridge handoff");
    auto mutexName = TaskBarWidgetsUpdateMutexName(j.install.wstring());
    Handle lock(CreateMutexW(nullptr, TRUE, mutexName.c_str()));
    Require(lock.h && GetLastError() != ERROR_ALREADY_EXISTS, "An update for this installation is already active");
    auto t = ReadJournal(j);
    Require(!t.bridge && t.phase == "complete", "Bridge continuation already started; use recovery");
    j.pids[0] = bridgePid;
    j.pids[1] = j.pids[2] = 0;
    auto bridge = OldProcesses(j);
    Require(Wait(bridge, ExitTimeout()) && Running(j).empty(), "Bridge did not exit normally");
    try
    {
        Require(Sha(j.install / L"app-files.json") == LowerA(j.manifestHash), "Installed bridge inventory changed");
        auto next = PrepareBridgeStage(j);
        auto files = Manifest(next.stage / L"app-files.json", j.version);
        Dependencies(next);
        SpaceAndPermissions(next, files);
        NotCancelled(j);
        // Never take a second snapshot of an original file or of personal data.
        // Extend the legacy journal only for newly introduced final program paths.
        t.bridge = true;
        t.phase = "prepared";
        SaveJournal(j, t);
        for (const auto &file : files)
            if (std::none_of(t.saved.begin(), t.saved.end(), [&](auto &s) {
                return s.group == "program" && LowerA(s.path) == LowerA(file.path); }))
                Snapshot(j, t, "program", file.path);
        UnlockedTargets(j, t);
        VerifyStage(next, files);
        Status(j.work, "installing");
        Apply(next, files, t);
        Require(DeleteFileW(FileApiPath(j.work / L"health.json").c_str()), "Cannot clear bridge health state");
        Status(j.work, "starting");
        auto process = Launch(next, true, &t.launched);
        t.phase = "starting";
        SaveJournal(j, t);
        if (!Healthy(j, process.h))
            throw Failure("Final application did not become healthy");
        t.phase = "complete";
        SaveJournal(j, t);
        Status(j.work, "complete");
        RotateOlderBackups(j);
        return 0;
    }
    catch (const std::exception &e)
    {
        try { Atomic(j.work / L"bridge-error.json", "{\"message\":" + Quote(e.what()) + "}"); } catch (...) {}
        try
        {
            Require(StopNew(j, t), "New application did not close; original backup retained");
            Rollback(j, t);
            RestartOld(j, lock);
            return 1;
        }
        catch (const std::exception &rollbackError)
        {
            Status(j.work, "recoveryRequired", std::string("Bridge rollback could not finish: ") + rollbackError.what());
            return 2;
        }
    }
}
