// ==========================================================================
// LibProsperoPKG macOS Studio — Frontend Client Controller
// ==========================================================================

(() => {
  'use strict';

  // --- State ---
  let isBuilding = false;
  let lastBuiltPackagePath = null;
  let lastExtractedDir = null;
  let inspectEntriesCache = [];

  // --- DOM Elements ---
  const tabButtons = document.querySelectorAll('.nav-tab');
  const tabPanes = document.querySelectorAll('.tab-pane');
  const globalStatusDot = document.getElementById('global-status-dot');
  const globalStatusText = document.getElementById('global-status-text');

  // Build Tab Elements
  const inputSource = document.getElementById('input-source');
  const btnBrowseSource = document.getElementById('btn-browse-source');
  const inputOutput = document.getElementById('input-output');
  const btnBrowseOutput = document.getElementById('btn-browse-output');
  const inputWorkdir = document.getElementById('input-workdir');
  const btnBrowseWorkdir = document.getElementById('btn-browse-workdir');
  const sourceBadge = document.getElementById('source-badge');
  const metadataBanner = document.getElementById('metadata-banner');
  const metaTitleText = document.getElementById('meta-title-text');
  const metaPlaygoText = document.getElementById('meta-playgo-text');
  const metaDiskText = document.getElementById('meta-disk-text');

  const inputContentId = document.getElementById('input-content-id');
  const inputTitle = document.getElementById('input-title');
  const inputVersion = document.getElementById('input-version');
  const inputPasscode = document.getElementById('input-passcode');
  const selectMode = document.getElementById('select-mode');
  const selectImageMode = document.getElementById('select-image-mode');
  const selectCompression = document.getElementById('select-compression');
  const inputChunks = document.getElementById('input-chunks');
  const selectCpuMode = document.getElementById('select-cpu-mode');
  const chkDeterministic = document.getElementById('chk-deterministic');
  const chkVerify = document.getElementById('chk-verify');
  const chkFself = document.getElementById('chk-fself');
  const chkBackport = document.getElementById('chk-backport');
  const selectBackportSdk = document.getElementById('select-backport-sdk');
  const backportSdkContainer = document.getElementById('backport-sdk-container');
  const chkAlreadyPatched = document.getElementById('chk-already-patched');

  const btnStageBackport = document.getElementById('btn-stage-backport');
  const btnStageBackportText = document.getElementById('btn-stage-backport-text');
  const fakelibSelectionContainer = document.getElementById('fakelib-selection-container');
  const fakelibCountBadge = document.getElementById('fakelib-count-badge');
  const fakelibList = document.getElementById('fakelib-list');
  const btnFakelibReset = document.getElementById('btn-fakelib-reset');
  const btnFakelibSelectAll = document.getElementById('btn-fakelib-select-all');
  const btnFakelibDeselectAll = document.getElementById('btn-fakelib-deselect-all');
  let currentFakelibs = [];

  const btnBuild = document.getElementById('btn-build');
  const btnCancel = document.getElementById('btn-cancel');
  const btnOpenOutput = document.getElementById('btn-open-output');
  const globalTopProgress = document.getElementById('global-top-progress');
  const globalTopProgressFill = document.getElementById('global-top-progress-fill');
  const topProgressBadge = document.getElementById('top-progress-badge');
  const topProgressDesc = document.getElementById('top-progress-desc');
  const topProgressPercent = document.getElementById('top-progress-percent');
  const terminalLog = document.getElementById('terminal-log');
  const btnCopyLog = document.getElementById('btn-copy-log');
  const btnClearLog = document.getElementById('btn-clear-log');
  const buildResultBanner = document.getElementById('build-result-banner');
  const resultTitle = document.getElementById('result-title');
  const resultMetrics = document.getElementById('result-metrics');

  // Inspect Tab Elements
  const inspectPkgPath = document.getElementById('inspect-pkg-path');
  const btnBrowseInspect = document.getElementById('btn-browse-inspect');
  const btnRunInspect = document.getElementById('btn-run-inspect');
  const inspectPlaceholder = document.getElementById('inspect-placeholder');
  const inspectResults = document.getElementById('inspect-results');
  const cardPkgType = document.getElementById('card-pkg-type');
  const cardPkgSize = document.getElementById('card-pkg-size');
  const cardContentId = document.getElementById('card-content-id');
  const cardEntriesCount = document.getElementById('card-entries-count');
  const fihKvList = document.getElementById('fih-kv-list');
  const cntKvList = document.getElementById('cnt-kv-list');
  const filterEntries = document.getElementById('filter-entries');
  const entriesTableBody = document.getElementById('entries-table-body');

  // Unpack & Verify Tab Elements
  const unpackPkgPath = document.getElementById('unpack-pkg-path');
  const btnBrowseUnpackPkg = document.getElementById('btn-browse-unpack-pkg');
  const unpackOutDir = document.getElementById('unpack-out-dir');
  const btnBrowseUnpackDir = document.getElementById('btn-browse-unpack-dir');
  const unpackPasscode = document.getElementById('unpack-passcode');
  const btnTogglePasscode = document.getElementById('btn-toggle-passcode');
  const btnRunQuickVerify = document.getElementById('btn-run-quick-verify');
  const btnRunUnpack = document.getElementById('btn-run-unpack');

  const infoValTitle = document.getElementById('info-val-title');
  const infoValContentId = document.getElementById('info-val-content-id');
  const infoValVersion = document.getElementById('info-val-version');
  const infoValSdk = document.getElementById('info-val-sdk');
  const infoValSysReq = document.getElementById('info-val-sys-req');
  const infoValLanguages = document.getElementById('info-val-languages');
  const infoValPlaygoLanguages = document.getElementById('info-val-playgo-languages');
  const infoValPlaygo = document.getElementById('info-val-playgo');
  const infoValDrm = document.getElementById('info-val-drm');
  const infoValContainer = document.getElementById('info-val-container');
  const infoValContentType = document.getElementById('info-val-content-type');
  const infoValSegments = document.getElementById('info-val-segments');
  const infoValPkgSize = document.getElementById('info-val-pkg-size');

  const pkgCoverImage = document.getElementById('pkg-cover-image');
  const pkgCoverEmpty = document.getElementById('pkg-cover-empty');

  const unpackTerminalLog = document.getElementById('unpack-terminal-log');
  const btnCopyUnpackLog = document.getElementById('btn-copy-unpack-log');
  const btnClearUnpackLog = document.getElementById('btn-clear-unpack-log');

  const unpackStatusBarFill = document.getElementById('unpack-status-bar-fill');
  const unpackStatusTextLabel = document.getElementById('unpack-status-text-label');

  // --- Photino Bridge Helper ---
  function sendToHost(action, payload = {}) {
    if (window.external && typeof window.external.sendMessage === 'function') {
      window.external.sendMessage(JSON.stringify({ action, ...payload }));
    } else {
      console.warn('[Bridge] window.external.sendMessage not available');
    }
  }

  if (window.external && typeof window.external.receiveMessage === 'function') {
    window.external.receiveMessage((raw) => {
      try {
        const msg = JSON.parse(raw);
        handleHostMessage(msg.type, msg.payload);
      } catch (e) {
        console.error('[Bridge] Failed to parse host message:', e, raw);
      }
    });
  }

  function resetBuildButtons() {
    isBuilding = false;
    btnBuild.style.display = 'inline-flex';
    btnBuild.disabled = false;
    if (btnStageBackport) {
      btnStageBackport.disabled = false;
      if (btnStageBackportText) btnStageBackportText.textContent = 'Backport & Parse Fakelibs';
    }
    if (btnCancel) {
      btnCancel.style.display = 'none';
      btnCancel.disabled = false;
      btnCancel.innerHTML = `<svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2.2"><circle cx="12" cy="12" r="10"></circle><line x1="15" y1="9" x2="9" y2="15"></line><line x1="9" y1="9" x2="15" y2="15"></line></svg><span>Cancel Build</span>`;
    }
  }

  // --- Host Message Router ---
  function handleHostMessage(type, data) {
    switch (type) {
      case 'nativeFilesDropped':
        handleNativeFilesDropped(data.paths, data.x, data.y);
        break;

      case 'folderSelected':
        if (data.target === 'source') {
          inputSource.value = data.path;
          if (!inputOutput.value) {
            inputOutput.value = data.path + '_output';
          }
        } else if (data.target === 'output') {
          inputOutput.value = data.path;
        } else if (data.target === 'workDir') {
          if (inputWorkdir) inputWorkdir.value = data.path;
        } else if (data.target === 'unpackDir') {
          unpackOutDir.value = data.path;
        }
        break;

      case 'fileSelected':
        if (data.target === 'inspect') {
          inspectPkgPath.value = data.path;
          runInspect(data.path);
        } else if (data.target === 'unpackPkg') {
          unpackPkgPath.value = data.path;
          if (!unpackOutDir.value) {
            unpackOutDir.value = data.path.replace(/\.pkg$/i, '') + '-unpacked';
          }
          loadPkgInfo(data.path);
        }
        break;

      case 'metadataScanned':
        metadataBanner.style.display = 'block';
        sourceBadge.textContent = data.hasParamJson ? 'param.json Found' : 'Minimal Metadata';
        sourceBadge.className = data.hasParamJson ? 'badge success' : 'badge';

        inputContentId.value = data.contentId;
        inputTitle.value = data.title;
        inputVersion.value = data.version;

        metaTitleText.textContent = `Title: ${data.title} (${data.titleId})`;
        metaPlaygoText.textContent = data.playgoStatus;

        if (metaDiskText) {
          if (data.sourceSizeFormatted && data.freeSpaceFormatted) {
            metaDiskText.style.display = 'inline-flex';
            if (data.hasEnoughSpace) {
              metaDiskText.className = 'meta-disk success';
              metaDiskText.innerHTML = `💾 Dump Size: <strong>${data.sourceSizeFormatted}</strong> • Free Disk: <strong>${data.freeSpaceFormatted}</strong> (Required: ~${data.requiredSpaceFormatted})`;
            } else {
              metaDiskText.className = 'meta-disk warning';
              metaDiskText.innerHTML = `⚠️ Low Free Space! Available: <strong>${data.freeSpaceFormatted}</strong> • Required: <strong>~${data.requiredSpaceFormatted}</strong> (Game: ${data.sourceSizeFormatted})`;
            }
          } else {
            metaDiskText.style.display = 'none';
          }
        }
        break;

      case 'buildStarted':
        isBuilding = true;
        setGlobalStatus('building', 'Building 0% • Stage 1/5');
        btnBuild.style.display = 'none';
        if (btnCancel) {
          btnCancel.style.display = 'inline-flex';
          btnCancel.disabled = false;
          btnCancel.innerHTML = `<svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2.2"><circle cx="12" cy="12" r="10"></circle><line x1="15" y1="9" x2="9" y2="15"></line><line x1="9" y1="9" x2="15" y2="15"></line></svg><span>Cancel Build</span>`;
        }
        btnOpenOutput.style.display = 'none';
        if (globalTopProgress) {
          globalTopProgress.style.display = 'block';
          if (globalTopProgressFill) globalTopProgressFill.style.width = '0%';
        }
        buildResultBanner.style.display = 'none';
        updateProgress(0, 'Stage 1/5', 'Starting build...');
        appendLog(`Build started: ${data.contentId} (${data.version})`, 'stage');
        break;

      case 'buildProgress':
        updateProgress(data.percent, data.stage, data.desc);
        break;

      case 'buildLog':
        appendLog(data.log, detectLogClass(data.log), data.timestamp);
        break;

      case 'backportCompleted':
        if (btnStageBackport) {
          btnStageBackport.disabled = false;
          if (btnStageBackportText) btnStageBackportText.textContent = 'Backport & Parse Fakelibs';
        }
        if (fakelibSelectionContainer) {
          fakelibSelectionContainer.style.display = 'block';
        }
        currentFakelibs = (data.fakelibs || []).filter(f => !f.fileName.toLowerCase().endsWith('.psp'));
        renderFakelibsList(currentFakelibs);
        setGlobalStatus('ready', `Backport complete • ${currentFakelibs.length} fakelibs parsed`);
        break;

      case 'backportError':
        if (btnStageBackport) {
          btnStageBackport.disabled = false;
          if (btnStageBackportText) btnStageBackportText.textContent = 'Backport & Parse Fakelibs';
        }
        alert(`Backport error: ${data.message}`);
        break;

      case 'fakelibToggled':
        if (data.fakelibs) {
          currentFakelibs = data.fakelibs.filter(f => !f.fileName.toLowerCase().endsWith('.psp'));
          renderFakelibsList(currentFakelibs);
        } else {
          updateFakelibStagedState(data.fileName, data.staged);
        }
        break;

      case 'fakelibsSynced':
        if (data.fakelibs) {
          currentFakelibs = data.fakelibs.filter(f => !f.fileName.toLowerCase().endsWith('.psp'));
          renderFakelibsList(currentFakelibs);
        }
        break;

      case 'resetFakelibs':
      case 'stageMissingFakelibs':
      case 'fakelibsUpdated':
        if (btnFakelibReset) {
          btnFakelibReset.disabled = false;
          btnFakelibReset.textContent = 'RST';
        }
        if (data.fakelibs) {
          currentFakelibs = data.fakelibs.filter(f => !f.fileName.toLowerCase().endsWith('.psp'));
          renderFakelibsList(currentFakelibs);
        }
        break;

      case 'buildCompleted':
        resetBuildButtons();
        setGlobalStatus('ready', 'Build Successful (100%)');
        updateProgress(100, 'Done', 'Build complete!');
        playCompletionSound();
        if (globalTopProgressFill) globalTopProgressFill.style.width = '100%';
        setTimeout(() => {
          if (!isBuilding) {
            if (globalTopProgress) globalTopProgress.style.display = 'none';
          }
        }, 2500);
        lastBuiltPackagePath = data.packagePath;
        btnOpenOutput.style.display = 'inline-flex';

        buildResultBanner.style.display = 'flex';
        buildResultBanner.className = 'result-banner';
        resultTitle.textContent = 'Package Built Successfully!';

        const sizeMb = (data.fileSize / (1024 * 1024)).toFixed(2);
        const durationSec = (data.durationMs / 1000).toFixed(2);
        let metricText = `Size: ${formatBytes(data.fileSize)} (${sizeMb} MB) • Time: ${durationSec}s`;

        if (data.verification && data.verification.passed) {
          metricText += ` • Verified (SHA: ${data.verification.sha256.substring(0, 12)}...)`;
        }
        resultMetrics.textContent = metricText;
        appendLog(`[DONE] Output: ${data.packagePath}`, 'success');
        break;

      case 'buildCancelled':
        resetBuildButtons();
        setGlobalStatus('ready', 'Build Cancelled');
        if (globalTopProgress) globalTopProgress.style.display = 'none';
        buildResultBanner.style.display = 'flex';
        buildResultBanner.className = 'result-banner warning';
        resultTitle.textContent = 'Build Cancelled';
        resultMetrics.textContent = data.message || 'Build was cancelled by user. Temporary files cleared.';
        appendLog('[CANCEL] ' + (data.message || 'Build was cancelled by user. Temporary files cleared.'), 'warning');
        break;

      case 'buildError':
        resetBuildButtons();
        setGlobalStatus('ready', 'Build Failed');
        if (globalTopProgress) globalTopProgress.style.display = 'none';

        buildResultBanner.style.display = 'flex';
        buildResultBanner.className = 'result-banner error';
        resultTitle.textContent = 'Build Failed';
        resultMetrics.textContent = data.message;
        appendLog(`[ERROR] ${data.message}`, 'error');
        break;

      case 'inspectResult':
        renderInspectResult(data);
        break;

      case 'inspectError':
        alert(`Inspection Error: ${data.message}`);
        break;

      case 'unpackVerifyLog':
        appendUnpackVerifyLog(data.line);
        break;

      case 'packageInfoResult':
        renderPackageInfo(data);
        break;

      case 'packageInfoError':
        setUnpackStatus('error', `Error: ${data.message}`);
        break;

      case 'quickVerifyResult':
        if (data.success) {
          setUnpackStatus('success', 'Quick verification passed');
        } else {
          setUnpackStatus('error', data.message || 'Verification failed');
        }
        break;

      case 'unpackResult':
        if (data.success) {
          setUnpackStatus('success', 'Unpacking complete');
        } else {
          setUnpackStatus('error', data.message || 'Unpacking failed');
        }
        break;

      case 'error':
        alert(`Error: ${data.message}`);
        break;
    }
  }

  // --- Tab Switching ---
  function switchTab(tabId) {
    tabButtons.forEach(b => {
      if (b.getAttribute('data-tab') === tabId) b.classList.add('active');
      else b.classList.remove('active');
    });
    tabPanes.forEach(p => {
      if (p.id === `pane-${tabId}`) p.classList.add('active');
      else p.classList.remove('active');
    });
  }

  tabButtons.forEach(btn => {
    btn.addEventListener('click', () => {
      const tabId = btn.getAttribute('data-tab');
      switchTab(tabId);
    });
  });

  // --- Build Tab Event Listeners ---
  btnBrowseSource.addEventListener('click', () => {
    sendToHost('browseFolder', { target: 'source' });
  });

  btnBrowseOutput.addEventListener('click', () => {
    sendToHost('browseFolder', { target: 'output' });
  });

  if (btnBrowseWorkdir) {
    btnBrowseWorkdir.addEventListener('click', () => {
      sendToHost('browseFolder', { target: 'workDir' });
    });
  }

  inputSource.addEventListener('change', () => {
    const path = inputSource.value.trim();
    if (fakelibSelectionContainer) fakelibSelectionContainer.style.display = 'none';
    if (fakelibList) fakelibList.innerHTML = '';
    currentFakelibs = [];
    if (path) {
      sendToHost('scanMetadata', { path });
      if (!inputOutput.value) {
        inputOutput.value = path + '_output';
      }
    }
  });

  // Stage Backport & Fakelibs Action Button
  if (btnStageBackport) {
    btnStageBackport.addEventListener('click', () => {
      const source = inputSource.value.trim();
      if (!source) {
        alert('Please select a source folder containing your game/app files first.');
        return;
      }
      btnStageBackport.disabled = true;
      if (btnStageBackportText) btnStageBackportText.textContent = 'Backporting & Scanning...';
      appendLog(`[Backport] Starting Stage 0 backport & fakelib scan for: ${source}`, 'stage');

      sendToHost('stageBackport', {
        source,
        targetSdk: selectBackportSdk ? selectBackportSdk.value : '0x0400000000000000',
        autoFself: chkFself ? chkFself.checked : true
      });
    });
  }

  if (btnFakelibReset) {
    btnFakelibReset.addEventListener('click', () => {
      const source = inputSource.value.trim();
      if (!source) return;
      btnFakelibReset.disabled = true;
      btnFakelibReset.textContent = '...';
      sendToHost('resetFakelibs', {
        source,
        targetSdk: selectBackportSdk?.value || '0x0400000000000000'
      });
    });
  }

  if (btnFakelibSelectAll) {
    btnFakelibSelectAll.addEventListener('click', () => {
      const source = inputSource.value.trim();
      if (!source) return;
      const allCheckboxes = document.querySelectorAll('.chk-fakelib-item');
      const allFiles = [];
      allCheckboxes.forEach(cb => {
        cb.checked = true;
        allFiles.push(cb.dataset.filename);
      });
      updateFakelibCount();
      sendToHost('syncFakelibs', {
        source,
        enabledFiles: allFiles,
        targetSdk: selectBackportSdk?.value || '0x0400000000000000'
      });
    });
  }

  if (btnFakelibDeselectAll) {
    btnFakelibDeselectAll.addEventListener('click', () => {
      const source = inputSource.value.trim();
      if (!source) return;
      const allCheckboxes = document.querySelectorAll('.chk-fakelib-item');
      allCheckboxes.forEach(cb => {
        cb.checked = false;
      });
      updateFakelibCount();
      sendToHost('syncFakelibs', {
        source,
        enabledFiles: [],
        targetSdk: selectBackportSdk?.value || '0x0400000000000000'
      });
    });
  }

  function renderFakelibsList(fakelibs) {
    if (!fakelibList) return;
    fakelibList.innerHTML = '';

    // Safety rule: Never display any .psp file to the user
    const displayList = (fakelibs || []).filter(f => !f.fileName.toLowerCase().endsWith('.psp'));

    if (fakelibCountBadge) {
      const stagedCount = displayList.filter(f => f.isStaged).length;
      fakelibCountBadge.textContent = `${stagedCount} of ${displayList.length} staged`;
      fakelibCountBadge.className = stagedCount > 0 ? 'badge success' : 'badge';
    }

    if (displayList.length === 0) {
      fakelibList.innerHTML = '<div style="font-size: 11.5px; color: var(--text-tertiary); padding: 8px 4px;">No compatibility fakelibs required for this game dump.</div>';
      return;
    }

    displayList.forEach(item => {
      const row = document.createElement('div');
      row.className = 'fakelib-item';
      row.id = `fakelib-item-${sanitizeDomId(item.fileName)}`;

      // Badges: REQUIRED / OPTIONAL
      let badgeHtml = '';
      if (item.isRequired) {
        badgeHtml = `<span class="badge-required" title="Imported by game ELF on target SDK">REQUIRED</span>`;
      } else if (item.isStaged) {
        badgeHtml = `<span class="badge-optional" title="Optional staged stub">OPTIONAL</span>`;
      }

      const checkedAttr = item.isStaged ? 'checked' : '';

      row.innerHTML = `
        <div class="fakelib-item-left">
          <label class="checkbox-container" style="margin: 0;">
            <input type="checkbox" class="chk-fakelib-item" data-filename="${escapeHtml(item.fileName)}" data-required="${item.isRequired ? '1' : '0'}" ${checkedAttr}>
            <span class="checkmark"></span>
          </label>
          <div class="fakelib-item-info">
            <div class="fakelib-item-name-wrap">
              <span class="fakelib-item-name">${escapeHtml(item.fileName)}</span>
              <span class="badge-subfolder">${escapeHtml(item.targetSubDir)}/</span>
              ${badgeHtml}
            </div>
            <div class="fakelib-item-desc">${escapeHtml(item.description || item.moduleName)}</div>
          </div>
        </div>
        <div class="fakelib-item-right">
          <span class="fakelib-item-size">${escapeHtml(item.sizeFormatted || '')}</span>
        </div>
      `;

      const chk = row.querySelector('.chk-fakelib-item');
      chk.addEventListener('change', () => {
        const source = inputSource.value.trim();
        if (!source) return;
        const isEnabled = chk.checked;
        sendToHost('toggleFakelib', {
          source,
          fileName: item.fileName,
          enabled: isEnabled,
          targetSdk: selectBackportSdk?.value || '0x0400000000000000'
        });
        updateFakelibCount();
      });

      fakelibList.appendChild(row);
    });
  }

  function updateFakelibStagedState(fileName, isStaged) {
    const chk = document.querySelector(`.chk-fakelib-item[data-filename="${fileName}"]`);
    if (chk) {
      chk.checked = isStaged;
    }
    updateFakelibCount();
  }

  function updateFakelibCount() {
    if (!fakelibCountBadge) return;
    const all = document.querySelectorAll('.chk-fakelib-item');
    const checked = document.querySelectorAll('.chk-fakelib-item:checked');
    fakelibCountBadge.textContent = `${checked.length} of ${all.length} staged`;
    fakelibCountBadge.className = checked.length > 0 ? 'badge success' : 'badge';
  }

  function sanitizeDomId(str) {
    return (str || '').replace(/[^a-zA-Z0-9_-]/g, '_');
  }

  btnBuild.addEventListener('click', () => {
    const source = inputSource.value.trim();
    const output = inputOutput.value.trim();
    const contentId = inputContentId.value.trim();
    const title = inputTitle.value.trim();
    const version = inputVersion.value.trim();
    const passcode = inputPasscode.value.trim();
    const mode = selectMode.value;
    const imageMode = selectImageMode.value;
    let compression = selectCompression.value;
    if (compression === 'zlib') compression = 'none';
    const chunks = parseInt(inputChunks.value, 10) || 1;
    const deterministic = chkDeterministic.checked;
    const verify = chkVerify.checked;
    const autoFself = chkFself ? chkFself.checked : true;

    if (!source) {
      alert('Please select a source folder containing your game/app files.');
      return;
    }
    if (!contentId) {
      alert('Please provide a valid Content ID (e.g. UP0000-PPSA00000_00-0000000000000000).');
      return;
    }

    const excludedFakelibs = Array.from(document.querySelectorAll('.chk-fakelib-item:not(:checked)'))
      .map(cb => cb.dataset.filename)
      .filter(Boolean);

    // Immediately switch UI to Building state & show Cancel button and progress bars
    isBuilding = true;
    btnBuild.style.display = 'none';
    if (btnStageBackport) btnStageBackport.disabled = true;
    if (btnCancel) {
      btnCancel.style.display = 'inline-flex';
      btnCancel.disabled = false;
      btnCancel.innerHTML = `<svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2.2"><circle cx="12" cy="12" r="10"></circle><line x1="15" y1="9" x2="9" y2="15"></line><line x1="9" y1="9" x2="15" y2="15"></line></svg><span>Cancel Build</span>`;
    }
    btnOpenOutput.style.display = 'none';
    if (buildResultBanner) buildResultBanner.style.display = 'none';
    if (globalTopProgress) globalTopProgress.style.display = 'block';

    updateProgress(0, 'Stage 0/5', 'Pre-processing game dump (FSELF)...');
    setGlobalStatus('building', 'Building 0% • Stage 0/5');

    sendToHost('startBuild', {
      source,
      output: output || './output',
      contentId,
      title,
      version: version || '01.00',
      passcode: passcode || '00000000000000000000000000000000',
      mode,
      imageMode,
      compression,
      chunks,
      cpuMode: selectCpuMode ? selectCpuMode.value : 'cool',
      deterministic,
      verify,
      autoFself: chkAlreadyPatched && chkAlreadyPatched.checked ? false : autoFself,
      autoBackport: chkAlreadyPatched && chkAlreadyPatched.checked ? false : (chkBackport ? chkBackport.checked : true),
      alreadyPatched: chkAlreadyPatched ? chkAlreadyPatched.checked : false,
      workDir: inputWorkdir ? inputWorkdir.value.trim() : '',
      targetSdk: selectBackportSdk ? selectBackportSdk.value : '0x0400000000000000',
      excludedFakelibs
    });
  });

  if (chkAlreadyPatched) {
    let savedFself = chkFself ? chkFself.checked : true;
    let savedBackport = chkBackport ? chkBackport.checked : true;

    chkAlreadyPatched.addEventListener('change', () => {
      const isPatched = chkAlreadyPatched.checked;
      if (isPatched) {
        if (chkFself) {
          savedFself = chkFself.checked;
          chkFself.checked = false;
          chkFself.disabled = true;
          if (chkFself.parentElement) chkFself.parentElement.style.opacity = '0.35';
        }
        if (chkBackport) {
          savedBackport = chkBackport.checked;
          chkBackport.checked = false;
          chkBackport.disabled = true;
          if (chkBackport.parentElement) chkBackport.parentElement.style.opacity = '0.35';
        }
        if (selectBackportSdk) selectBackportSdk.disabled = true;
        if (backportSdkContainer) {
          backportSdkContainer.style.opacity = '0.3';
          backportSdkContainer.style.pointerEvents = 'none';
        }
        if (btnStageBackport) {
          btnStageBackport.disabled = true;
          btnStageBackport.style.opacity = '0.35';
        }
      } else {
        if (chkFself) {
          chkFself.disabled = false;
          chkFself.checked = savedFself;
          if (chkFself.parentElement) chkFself.parentElement.style.opacity = '1';
        }
        if (chkBackport) {
          chkBackport.disabled = false;
          chkBackport.checked = savedBackport;
          if (chkBackport.parentElement) chkBackport.parentElement.style.opacity = '1';
        }
        if (selectBackportSdk) selectBackportSdk.disabled = !savedBackport;
        if (backportSdkContainer) {
          backportSdkContainer.style.opacity = savedBackport ? '1' : '0.45';
          backportSdkContainer.style.pointerEvents = savedBackport ? 'auto' : 'none';
        }
        if (btnStageBackport) {
          btnStageBackport.disabled = false;
          btnStageBackport.style.opacity = '1';
        }
      }
    });
  }

  if (chkBackport && selectBackportSdk) {
    chkBackport.addEventListener('change', () => {
      selectBackportSdk.disabled = !chkBackport.checked;
      if (backportSdkContainer) {
        backportSdkContainer.style.opacity = chkBackport.checked ? '1' : '0.45';
        backportSdkContainer.style.pointerEvents = chkBackport.checked ? 'auto' : 'none';
      }
    });
  }

  if (btnCancel) {
    btnCancel.addEventListener('click', () => {
      btnCancel.disabled = true;
      btnCancel.innerHTML = `<svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2.2" class="spin"><circle cx="12" cy="12" r="10" stroke-dasharray="31.4" stroke-dashoffset="10"></circle></svg><span>Cancelling...</span>`;
      sendToHost('cancelBuild');

      // Safety UI recovery timeout: if host does not respond within 5s, unlock UI
      setTimeout(() => {
        if (!isBuilding) return;
        resetBuildButtons();
        setGlobalStatus('ready', 'Build Cancelled');
        if (globalTopProgress) globalTopProgress.style.display = 'none';
        appendLog('[CANCEL] UI reset after cancellation request.', 'warning');
      }, 5000);
    });
  }

  function updateProgress(percent, stage, desc) {
    const clamped = Math.max(0, Math.min(100, Math.round(percent)));
    if (globalTopProgressFill) globalTopProgressFill.style.width = clamped + '%';
    if (topProgressPercent) topProgressPercent.textContent = clamped + '% Total';
    if (topProgressBadge && stage) topProgressBadge.textContent = stage;
    if (topProgressDesc && desc) topProgressDesc.textContent = desc;

    if (isBuilding) {
      // Safety unhide: ensure top progress bar and cancel button are visible while building
      if (globalTopProgress && globalTopProgress.style.display === 'none') {
        globalTopProgress.style.display = 'block';
      }
      if (btnCancel && btnCancel.style.display === 'none') {
        btnCancel.style.display = 'inline-flex';
        btnBuild.style.display = 'none';
      }
      setGlobalStatus('building', `Building ${clamped}% Total${stage ? ' • ' + stage : ''}`);
    }
  }

  btnOpenOutput.addEventListener('click', () => {
    if (lastBuiltPackagePath) {
      sendToHost('openFolder', { path: lastBuiltPackagePath });
    }
  });

  btnClearLog.addEventListener('click', () => {
    terminalLog.innerHTML = '';
  });

  btnCopyLog.addEventListener('click', () => {
    const text = terminalLog.innerText;
    navigator.clipboard.writeText(text).then(() => {
      alert('Console log copied to clipboard!');
    });
  });

  // --- Inspect Tab Event Listeners ---
  btnBrowseInspect.addEventListener('click', () => {
    sendToHost('browseFile', { target: 'inspect' });
  });

  btnRunInspect.addEventListener('click', () => {
    const path = inspectPkgPath.value.trim();
    if (path) runInspect(path);
  });

  function runInspect(path) {
    sendToHost('inspectPkg', { path });
  }

  function renderInspectResult(data) {
    inspectPlaceholder.style.display = 'none';
    inspectResults.style.display = 'block';

    cardPkgType.textContent = data.type;
    cardPkgSize.textContent = `${formatBytes(data.size)} (${(data.size / (1024 * 1024)).toFixed(1)} MB)`;
    cardContentId.textContent = data.header ? data.header.contentId : 'N/A';
    cardEntriesCount.textContent = data.entries.length;

    // FIH KV List
    fihKvList.innerHTML = '';
    if (data.fih) {
      addKv(fihKvList, 'Signed Byte', data.fih.signedByte);
      addKv(fihKvList, 'PFS Offset', data.fih.pfsOffset);
      addKv(fihKvList, 'PFS Size', `${formatBytes(data.fih.pfsSize)}`);
      addKv(fihKvList, 'Embedded CNT', data.fih.embeddedCntOffset);
      addKv(fihKvList, 'Inner Blocks', data.fih.innerBlocks.toLocaleString());
      addKv(fihKvList, 'Metadata Blocks', data.fih.metadataBlocks.toLocaleString());
    } else {
      fihKvList.innerHTML = '<li><span class="key">FIH Header</span><span class="val">None (Meta Container)</span></li>';
    }

    // CNT KV List
    cntKvList.innerHTML = '';
    if (data.header) {
      addKv(cntKvList, 'Content ID', data.header.contentId);
      addKv(cntKvList, 'Flags', data.header.flags);
      addKv(cntKvList, 'DRM Type', data.header.drmType);
      addKv(cntKvList, 'Entry Count', data.header.entryCount);
      addKv(cntKvList, 'Body Size', `${formatBytes(data.header.bodySize)}`);
    }

    // Entries Table
    inspectEntriesCache = data.entries || [];
    renderEntriesTable(inspectEntriesCache);
  }

  function renderEntriesTable(entries) {
    entriesTableBody.innerHTML = '';
    entries.forEach(e => {
      const tr = document.createElement('tr');
      tr.innerHTML = `
        <td>${e.index}</td>
        <td><strong>${escapeHtml(e.name)}</strong></td>
        <td>${escapeHtml(e.id)}</td>
        <td><code>${e.rawId}</code></td>
        <td><code>${e.offset}</code></td>
        <td>${formatBytes(e.size)}</td>
        <td><span class="badge ${e.encrypted ? 'danger' : 'success'}">${e.encrypted ? 'YES' : 'NO'}</span></td>
      `;
      entriesTableBody.appendChild(tr);
    });
  }

  filterEntries.addEventListener('input', (ev) => {
    const q = ev.target.value.toLowerCase();
    const filtered = inspectEntriesCache.filter(e =>
      e.name.toLowerCase().includes(q) ||
      e.id.toLowerCase().includes(q) ||
      e.rawId.toLowerCase().includes(q)
    );
    renderEntriesTable(filtered);
  });

  // --- Unpack & Verify Event Listeners & Functions ---
  btnBrowseUnpackPkg.addEventListener('click', () => {
    sendToHost('browseFile', { target: 'unpackPkg' });
  });

  btnBrowseUnpackDir.addEventListener('click', () => {
    sendToHost('browseFolder', { target: 'unpackDir' });
  });

  unpackPkgPath.addEventListener('change', () => {
    const path = unpackPkgPath.value.trim();
    if (path) {
      if (!unpackOutDir.value) {
        unpackOutDir.value = path.replace(/\.pkg$/i, '') + '-unpacked';
      }
      loadPkgInfo(path);
    }
  });

  btnTogglePasscode.addEventListener('click', () => {
    unpackPasscode.type = unpackPasscode.type === 'password' ? 'text' : 'password';
  });

  btnRunQuickVerify.addEventListener('click', () => {
    const path = unpackPkgPath.value.trim();
    if (!path) {
      alert('Please select a PKG file to verify.');
      return;
    }
    const passcode = unpackPasscode.value.trim() || '00000000000000000000000000000000';
    setUnpackStatus('active', 'Running quick verification...');
    sendToHost('runQuickVerify', { path, passcode });
  });

  btnRunUnpack.addEventListener('click', () => {
    const path = unpackPkgPath.value.trim();
    if (!path) {
      alert('Please select a PKG file to unpack.');
      return;
    }
    const output = unpackOutDir.value.trim();
    const passcode = unpackPasscode.value.trim() || '00000000000000000000000000000000';
    setUnpackStatus('active', 'Unpacking package...');
    sendToHost('unpackPkg', { path, output, passcode });
  });

  btnCopyUnpackLog.addEventListener('click', () => {
    const entries = Array.from(unpackTerminalLog.querySelectorAll('.log-entry'));
    const text = entries.map(e => e.textContent).join('\n');
    navigator.clipboard.writeText(text);
  });

  btnClearUnpackLog.addEventListener('click', () => {
    unpackTerminalLog.innerHTML = '<div class="log-entry system">[SYSTEM] Log cleared.</div>';
  });

  function loadPkgInfo(path) {
    if (!path) return;
    setUnpackStatus('active', 'Reading package information...');
    const passcode = unpackPasscode.value.trim() || '00000000000000000000000000000000';
    sendToHost('loadPackageInfo', { path, passcode });
  }

  function appendUnpackVerifyLog(line) {
    const entry = document.createElement('div');
    entry.className = 'log-entry';
    if (line.includes('ERROR:') || line.includes('Error:')) entry.classList.add('error');
    else if (line.includes('RESULT: no errors found') || line.includes('RESULT: No errors found.') || line.includes('success') || line.includes('successfully')) entry.classList.add('success');
    entry.textContent = line;
    unpackTerminalLog.appendChild(entry);
    unpackTerminalLog.scrollTop = unpackTerminalLog.scrollHeight;
  }

  function setUnpackStatus(state, text) {
    unpackStatusBarFill.className = 'unpack-status-bar-fill' + (state ? ' ' + state : '');
    unpackStatusTextLabel.className = 'unpack-status-text-label' + (state ? ' ' + state : '');
    unpackStatusTextLabel.textContent = text;
  }

  function renderPackageInfo(data) {
    infoValTitle.textContent = data.title || '—';
    infoValContentId.textContent = data.contentId || '—';
    infoValVersion.textContent = data.version || '—';
    infoValSdk.textContent = data.sdk || '—';
    infoValSysReq.textContent = data.sysReq || '—';
    infoValLanguages.textContent = data.availableLanguages || '—';
    infoValPlaygoLanguages.textContent = data.playgoLanguages || '—';
    infoValPlaygo.textContent = data.playgoSummary || '—';
    infoValDrm.textContent = data.drm || '—';
    infoValContainer.textContent = data.container || '—';
    infoValContentType.textContent = data.contentType || '—';
    infoValSegments.textContent = data.segments || '—';
    infoValPkgSize.textContent = data.pkgSize || '—';

    if (data.coverImage) {
      pkgCoverImage.src = data.coverImage;
      pkgCoverImage.style.display = 'block';
      pkgCoverEmpty.style.display = 'none';
    } else {
      pkgCoverImage.style.display = 'none';
      pkgCoverEmpty.style.display = 'flex';
    }

    setUnpackStatus('', 'Package information loaded');
  }

  // --- Helper Functions ---
  function appendLog(message, cls = '', timestamp = null) {
    const entry = document.createElement('div');
    entry.className = `log-entry ${cls}`;
    const time = timestamp || new Date().toTimeString().split(' ')[0];
    entry.innerHTML = `<span class="time">[${time}]</span> ${escapeHtml(message)}`;
    terminalLog.appendChild(entry);
    terminalLog.scrollTop = terminalLog.scrollHeight;
  }

  function detectLogClass(line) {
    if (line.includes('[stage') || line.includes('Starting package') || line.includes('Preparing')) return 'stage';
    if (line.includes('WARNING') || line.includes('[!]')) return 'warning';
    if (line.includes('ERROR') || line.includes('failed')) return 'error';
    if (line.includes('Done') || line.includes('finished') || line.includes('successful')) return 'success';
    return '';
  }

  function setGlobalStatus(state, text) {
    globalStatusDot.className = `status-indicator ${state}`;
    globalStatusText.textContent = text;
  }

  function addKv(listEl, key, val) {
    const li = document.createElement('li');
    li.innerHTML = `<span class="key">${escapeHtml(key)}</span><span class="val">${escapeHtml(String(val))}</span>`;
    listEl.appendChild(li);
  }

  function formatBytes(bytes) {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
  }

  function escapeHtml(str) {
    if (!str) return '';
    return str.replace(/[&<>'"]/g, tag => ({
      '&': '&amp;',
      '<': '&lt;',
      '>': '&gt;',
      "'": '&#39;',
      '"': '&quot;'
    }[tag] || tag));
  }

  // --- Drag and Drop Management (Native macOS Bridge + WebKit Event Handlers) ---
  let activeHoverTargetKey = null;
  let activeHoverTimestamp = 0;
  let lastDroppedTargetKey = null;
  let lastDroppedTimestamp = 0;

  function flashInput(element) {
    if (!element) return;
    element.classList.remove('input-drop-highlight');
    void element.offsetWidth; // Force CSS reflow
    element.classList.add('input-drop-highlight');
    setTimeout(() => {
      element.classList.remove('input-drop-highlight');
    }, 850);
  }

  function applyDropAction(targetKey, path) {
    if (!path) return;
    console.log(`[DragDrop] Applying dropped path "${path}" to target "${targetKey}"`);

    switch (targetKey) {
      case 'source':
        if (inputSource) {
          inputSource.value = path;
          inputSource.dispatchEvent(new Event('change'));
          flashInput(inputSource);
          if (inputOutput && !inputOutput.value) {
            inputOutput.value = path + '_output';
            inputOutput.dispatchEvent(new Event('change'));
          }
        }
        break;

      case 'output':
        if (inputOutput) {
          inputOutput.value = path;
          inputOutput.dispatchEvent(new Event('change'));
          flashInput(inputOutput);
        }
        break;

      case 'inspect':
        if (inspectPkgPath) {
          inspectPkgPath.value = path;
          inspectPkgPath.dispatchEvent(new Event('change'));
          flashInput(inspectPkgPath);
        }
        runInspect(path);
        break;

      case 'unpackPkg':
        if (unpackPkgPath) {
          unpackPkgPath.value = path;
          unpackPkgPath.dispatchEvent(new Event('change'));
          flashInput(unpackPkgPath);
        }
        if (unpackOutDir && !unpackOutDir.value) {
          unpackOutDir.value = path.replace(/\.pkg$/i, '') + '-unpacked';
          unpackOutDir.dispatchEvent(new Event('change'));
        }
        loadPkgInfo(path);
        break;

      case 'unpackDir':
        if (unpackOutDir) {
          unpackOutDir.value = path;
          unpackOutDir.dispatchEvent(new Event('change'));
          flashInput(unpackOutDir);
        }
        break;
    }
  }

  function handleNativeFilesDropped(paths, x, y) {
    // Clear all lingering visual states
    document.querySelectorAll('.drag-over').forEach(el => el.classList.remove('drag-over'));

    if (!paths || paths.length === 0) return;
    const path = paths[0];
    const isPkg = /\.pkg$/i.test(path);

    console.log(`[DragDrop] Native drop received: "${path}" at (${x}, ${y})`);

    let chosenTarget = null;
    const now = Date.now();

    // 1. Check if a specific target element registered a recent drop or dragover
    if (lastDroppedTargetKey && (now - lastDroppedTimestamp < 1500)) {
      chosenTarget = lastDroppedTargetKey;
    } else if (activeHoverTargetKey && (now - activeHoverTimestamp < 1500)) {
      chosenTarget = activeHoverTargetKey;
    }

    // 2. Use document.elementFromPoint if coordinates were provided
    if (!chosenTarget && x > 0 && y > 0) {
      const el = document.elementFromPoint(x, y);
      if (el) {
        if (el.closest('#input-source, [data-drop-target="source"]')) {
          chosenTarget = 'source';
        } else if (el.closest('#input-output, [data-drop-target="output"]')) {
          chosenTarget = 'output';
        } else if (el.closest('#inspect-pkg-path, #inspect-placeholder, [data-drop-target="inspect"], #pane-inspect')) {
          chosenTarget = 'inspect';
        } else if (el.closest('#unpack-pkg-path, [data-drop-target="unpackPkg"]')) {
          chosenTarget = 'unpackPkg';
        } else if (el.closest('#unpack-out-dir, [data-drop-target="unpackDir"]')) {
          chosenTarget = 'unpackDir';
        }
      }
    }

    // 3. Fallback based on active tab and file extension
    if (!chosenTarget) {
      const activePane = document.querySelector('.tab-pane.active');
      const activeTabId = activePane ? activePane.id : 'pane-build';

      if (activeTabId === 'pane-inspect') {
        chosenTarget = 'inspect';
      } else if (activeTabId === 'pane-unpack-verify') {
        chosenTarget = isPkg ? 'unpackPkg' : 'unpackDir';
      } else { // pane-build
        if (isPkg) {
          switchTab('inspect');
          chosenTarget = 'inspect';
        } else {
          chosenTarget = 'source';
        }
      }
    }

    applyDropAction(chosenTarget, path);

    // Reset tracking keys
    activeHoverTargetKey = null;
    lastDroppedTargetKey = null;
  }

  function extractPathFromDrop(e) {
    if (!e.dataTransfer) return null;

    // 1. Desktop webview with file.path (Chromium, Electron, patched webviews)
    if (e.dataTransfer.files && e.dataTransfer.files.length > 0) {
      const file = e.dataTransfer.files[0];
      if (file.path) return file.path;
    }

    // 2. text/uri-list
    const uriList = e.dataTransfer.getData('text/uri-list');
    if (uriList) {
      const lines = uriList.split(/\r?\n/);
      for (const line of lines) {
        const trimmed = line.trim();
        if (trimmed.startsWith('file://')) {
          try {
            const url = new URL(trimmed);
            let p = decodeURIComponent(url.pathname);
            if (/^\/[a-zA-Z]:/.test(p)) p = p.substring(1);
            return p;
          } catch {
            let p = decodeURIComponent(trimmed.replace(/^file:\/\//, ''));
            if (/^\/[a-zA-Z]:/.test(p)) p = p.substring(1);
            return p;
          }
        }
      }
    }

    // 3. text/plain
    const text = e.dataTransfer.getData('text/plain') || e.dataTransfer.getData('text');
    if (text) {
      const trimmed = text.trim();
      if (trimmed.startsWith('file://')) {
        try {
          const url = new URL(trimmed);
          let p = decodeURIComponent(url.pathname);
          if (/^\/[a-zA-Z]:/.test(p)) p = p.substring(1);
          return p;
        } catch {
          return decodeURIComponent(trimmed.replace(/^file:\/\//, ''));
        }
      }
      if (trimmed.startsWith('/') || /^[a-zA-Z]:[\\\/]/.test(trimmed)) {
        return trimmed;
      }
    }

    // 4. DataTransferItemList
    if (e.dataTransfer.items && e.dataTransfer.items.length > 0) {
      for (let i = 0; i < e.dataTransfer.items.length; i++) {
        const item = e.dataTransfer.items[i];
        if (item.kind === 'file') {
          const file = item.getAsFile();
          if (file && file.path) return file.path;
        }
      }
    }

    return null;
  }

  function enableDragAndDrop(element, targetKey, wrapper = null) {
    if (!element) return;
    const targets = [element];
    if (wrapper && wrapper !== element) targets.push(wrapper);

    targets.forEach(targetEl => {
      targetEl.setAttribute('data-drop-target', targetKey);

      ['dragenter', 'dragover'].forEach(eventName => {
        targetEl.addEventListener(eventName, (e) => {
          e.preventDefault();
          e.stopPropagation();
          if (e.dataTransfer) {
            e.dataTransfer.dropEffect = 'copy';
          }
          activeHoverTargetKey = targetKey;
          activeHoverTimestamp = Date.now();
          targetEl.classList.add('drag-over');
          element.classList.add('drag-over');
        }, false);
      });

      ['dragleave', 'dragend'].forEach(eventName => {
        targetEl.addEventListener(eventName, (e) => {
          if (e.relatedTarget && targetEl.contains(e.relatedTarget)) {
            return;
          }
          targetEl.classList.remove('drag-over');
          element.classList.remove('drag-over');
        }, false);
      });

      targetEl.addEventListener('drop', (e) => {
        e.preventDefault();
        e.stopPropagation();
        targetEl.classList.remove('drag-over');
        element.classList.remove('drag-over');

        lastDroppedTargetKey = targetKey;
        lastDroppedTimestamp = Date.now();

        // Also attempt HTML5 extraction if browser supports it
        const path = extractPathFromDrop(e);
        if (path) {
          applyDropAction(targetKey, path);
        }
      }, false);
    });
  }

  // Prevent default window drop navigation
  window.addEventListener('dragover', (e) => {
    e.preventDefault();
  }, false);
  window.addEventListener('drop', (e) => {
    e.preventDefault();
    document.querySelectorAll('.drag-over').forEach(el => el.classList.remove('drag-over'));
  }, false);

  // Wire Drag & Drop for all Directory and File input fields
  if (inputSource) {
    enableDragAndDrop(inputSource, 'source', inputSource.closest('.form-group'));
  }

  if (inputOutput) {
    enableDragAndDrop(inputOutput, 'output', inputOutput.closest('.form-group'));
  }

  if (inspectPkgPath) {
    enableDragAndDrop(inspectPkgPath, 'inspect', inspectPkgPath.closest('.input-with-button'));
  }

  if (inspectPlaceholder) {
    enableDragAndDrop(inspectPlaceholder, 'inspect');
  }

  if (unpackPkgPath) {
    enableDragAndDrop(unpackPkgPath, 'unpackPkg', unpackPkgPath.closest('.unpack-form-row'));
  }

  if (unpackOutDir) {
    enableDragAndDrop(unpackOutDir, 'unpackDir', unpackOutDir.closest('.unpack-form-row'));
  }

  // Completion sound (CloneDVD / ImgBurn style triumphant success chime)
  function playCompletionSound() {
    try {
      const audio = new Audio('sounds/success.wav');
      audio.volume = 0.85;
      const playPromise = audio.play();
      if (playPromise !== undefined) {
        playPromise.catch(() => {
          playSynthesizedChime();
        });
      }
    } catch {
      playSynthesizedChime();
    }
  }

  function playSynthesizedChime() {
    try {
      const AudioCtx = window.AudioContext || window.webkitAudioContext;
      if (!AudioCtx) return;
      const ctx = new AudioCtx();
      if (ctx.state === 'suspended') ctx.resume();

      const notes = [
        { f: 523.25, start: 0.00, dur: 0.35, vol: 0.25 }, // C5
        { f: 659.25, start: 0.12, dur: 0.40, vol: 0.28 }, // E5
        { f: 783.99, start: 0.24, dur: 0.45, vol: 0.30 }, // G5
        { f: 1046.50, start: 0.38, dur: 1.20, vol: 0.40 }, // C6
        { f: 523.25, start: 0.38, dur: 1.10, vol: 0.15 },
        { f: 659.25, start: 0.38, dur: 1.10, vol: 0.15 }
      ];

      const now = ctx.currentTime;
      notes.forEach(n => {
        const osc = ctx.createOscillator();
        const gain = ctx.createGain();
        osc.type = 'triangle';
        osc.frequency.setValueAtTime(n.f, now + n.start);

        gain.gain.setValueAtTime(0.001, now + n.start);
        gain.gain.exponentialRampToValueAtTime(n.vol, now + n.start + 0.02);
        gain.gain.exponentialRampToValueAtTime(0.0001, now + n.start + n.dur);

        osc.connect(gain);
        gain.connect(ctx.destination);

        osc.start(now + n.start);
        osc.stop(now + n.start + n.dur);
      });
    } catch { }
  }

})();
