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
  const sourceBadge = document.getElementById('source-badge');
  const metadataBanner = document.getElementById('metadata-banner');
  const metaTitleText = document.getElementById('meta-title-text');
  const metaPlaygoText = document.getElementById('meta-playgo-text');

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
    if (btnCancel) {
      btnCancel.style.display = 'none';
      btnCancel.disabled = false;
      btnCancel.innerHTML = `<svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2.2"><circle cx="12" cy="12" r="10"></circle><line x1="15" y1="9" x2="9" y2="15"></line><line x1="9" y1="9" x2="15" y2="15"></line></svg><span>Cancel Build</span>`;
    }
  }

  // --- Host Message Router ---
  function handleHostMessage(type, data) {
    switch (type) {
      case 'folderSelected':
        if (data.target === 'source') {
          inputSource.value = data.path;
          if (!inputOutput.value) {
            inputOutput.value = data.path + '_output';
          }
        } else if (data.target === 'output') {
          inputOutput.value = data.path;
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

      case 'buildCompleted':
        resetBuildButtons();
        setGlobalStatus('ready', 'Build Successful (100%)');
        updateProgress(100, 'Done', 'Build complete!');
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
  tabButtons.forEach(btn => {
    btn.addEventListener('click', () => {
      const tabId = btn.getAttribute('data-tab');
      tabButtons.forEach(b => b.classList.remove('active'));
      tabPanes.forEach(p => p.classList.remove('active'));

      btn.classList.add('active');
      const pane = document.getElementById(`pane-${tabId}`);
      if (pane) pane.classList.add('active');
    });
  });

  // --- Build Tab Event Listeners ---
  btnBrowseSource.addEventListener('click', () => {
    sendToHost('browseFolder', { target: 'source' });
  });

  btnBrowseOutput.addEventListener('click', () => {
    sendToHost('browseFolder', { target: 'output' });
  });

  inputSource.addEventListener('change', () => {
    const path = inputSource.value.trim();
    if (path) {
      sendToHost('scanMetadata', { path });
      if (!inputOutput.value) {
        inputOutput.value = path + '_output';
      }
    }
  });

  btnBuild.addEventListener('click', () => {
    const source = inputSource.value.trim();
    const output = inputOutput.value.trim();
    const contentId = inputContentId.value.trim();
    const title = inputTitle.value.trim();
    const version = inputVersion.value.trim();
    const passcode = inputPasscode.value.trim();
    const mode = selectMode.value;
    const imageMode = selectImageMode.value;
    const compression = selectCompression.value;
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

    // Immediately switch UI to Building state & show Cancel button and progress bars
    isBuilding = true;
    btnBuild.style.display = 'none';
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
      autoFself,
      autoBackport: chkBackport ? chkBackport.checked : true
    });
  });

  if (btnCancel) {
    btnCancel.addEventListener('click', () => {
      btnCancel.disabled = true;
      btnCancel.innerHTML = `<span>Cancelling...</span>`;
      sendToHost('cancelBuild');
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

})();
