const request = (method, params = undefined) => fetch("/mcp", {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify({
    jsonrpc: "2.0",
    id: crypto.randomUUID(),
    method,
    params
  })
}).then(async response => {
  const payload = await response.json();
  if (payload.error) {
    throw new Error(payload.error.message);
  }

  return payload.result;
});

const elements = {
  workspaceId: document.querySelector("#workspaceId"),
  openWorkspace: document.querySelector("#openWorkspace"),
  toolName: document.querySelector("#toolName"),
  query: document.querySelector("#query"),
  businessRule: document.querySelector("#businessRule"),
  codeQuery: document.querySelector("#codeQuery"),
  maxResults: document.querySelector("#maxResults"),
  response: document.querySelector("#response"),
  workspaces: document.querySelector("#workspaces"),
  repositories: document.querySelector("#repositories"),
  documents: document.querySelector("#documents"),
  compareFields: document.querySelector("#compareFields"),
  newWorkspaceId: document.querySelector("#newWorkspaceId"),
  repositoryName: document.querySelector("#repositoryName"),
  repositoryPath: document.querySelector("#repositoryPath"),
  repositoryRemote: document.querySelector("#repositoryRemote"),
  repositoryBranch: document.querySelector("#repositoryBranch"),
  documentCategory: document.querySelector("#documentCategory"),
  documentFile: document.querySelector("#documentFile"),
  workspaceCountBadge: document.querySelector("#workspaceCountBadge"),
  heroWorkspaceTitle: document.querySelector("#heroWorkspaceTitle"),
  heroWorkspaceSubtitle: document.querySelector("#heroWorkspaceSubtitle"),
  metricRepositories: document.querySelector("#metricRepositories"),
  metricDocuments: document.querySelector("#metricDocuments"),
  metricJobs: document.querySelector("#metricJobs"),
  metricLastJobStatus: document.querySelector("#metricLastJobStatus"),
  overviewCards: document.querySelector("#overviewCards"),
  overviewJobs: document.querySelector("#overviewJobs"),
  overviewRootEntries: document.querySelector("#overviewRootEntries"),
  openJobsView: document.querySelector("#openJobsView"),
  openExplorerRoot: document.querySelector("#openExplorerRoot"),
  repositoriesBadge: document.querySelector("#repositoriesBadge"),
  documentsBadge: document.querySelector("#documentsBadge"),
  jobStatusFilter: document.querySelector("#jobStatusFilter"),
  jobSearchFilter: document.querySelector("#jobSearchFilter"),
  jobPageSize: document.querySelector("#jobPageSize"),
  jobsTableBody: document.querySelector("#jobsTableBody"),
  jobsPaginationSummary: document.querySelector("#jobsPaginationSummary"),
  jobsPrevPage: document.querySelector("#jobsPrevPage"),
  jobsPageIndicator: document.querySelector("#jobsPageIndicator"),
  jobsNextPage: document.querySelector("#jobsNextPage"),
  runningIndexingBadge: document.querySelector("#runningIndexingBadge"),
  runningIndexingList: document.querySelector("#runningIndexingList"),
  jobLogsBadge: document.querySelector("#jobLogsBadge"),
  jobLogsSelector: document.querySelector("#jobLogsSelector"),
  jobLogsSummary: document.querySelector("#jobLogsSummary"),
  jobLogsList: document.querySelector("#jobLogsList"),
  viewTabs: Array.from(document.querySelectorAll(".view-tab")),
  viewPanels: Array.from(document.querySelectorAll(".view-panel")),
  includeHiddenEntries: document.querySelector("#includeHiddenEntries"),
  explorerUp: document.querySelector("#explorerUp"),
  refreshExplorer: document.querySelector("#refreshExplorer"),
  explorerBreadcrumbs: document.querySelector("#explorerBreadcrumbs"),
  explorerCurrentPath: document.querySelector("#explorerCurrentPath"),
  explorerEntryCount: document.querySelector("#explorerEntryCount"),
  explorerEntries: document.querySelector("#explorerEntries"),
  previewTitle: document.querySelector("#previewTitle"),
  previewMeta: document.querySelector("#previewMeta"),
  filePreview: document.querySelector("#filePreview"),
  refreshGraphify: document.querySelector("#refreshGraphify"),
  openGraphifyExternal: document.querySelector("#openGraphifyExternal"),
  graphifyStatusBadge: document.querySelector("#graphifyStatusBadge"),
  graphifyStatusText: document.querySelector("#graphifyStatusText"),
  graphifyEmptyState: document.querySelector("#graphifyEmptyState"),
  graphifyFrame: document.querySelector("#graphifyFrame"),
  graphifySourceSelector: document.querySelector("#graphifySourceSelector"),
  graphifyGenerator: document.querySelector("#graphifyGenerator"),
  graphifyCounts: document.querySelector("#graphifyCounts"),
  graphifyCommand: document.querySelector("#graphifyCommand"),
  graphifyArguments: document.querySelector("#graphifyArguments"),
  graphifyProcessMeta: document.querySelector("#graphifyProcessMeta"),
  graphifyProcessError: document.querySelector("#graphifyProcessError"),
  graphifyStderr: document.querySelector("#graphifyStderr"),
  graphifyStdout: document.querySelector("#graphifyStdout")
};

const state = {
  activeView: "overview",
  workspaces: [],
  repositories: [],
  documents: [],
  jobs: [],
  explorer: null,
  preview: null,
  graphify: null,
  graphifySelectedPath: null,
  includeHiddenEntries: false,
  explorerPath: "",
  jobPage: 1,
  selectedJobId: null
};

let jobsPollingHandle = null;

const setResponse = value => {
  elements.response.textContent = typeof value === "string" ? value : JSON.stringify(value, null, 2);
};

const formatDateTime = value => value ? new Date(value).toLocaleString("pt-BR") : "-";

const formatSize = value => {
  if (typeof value !== "number") {
    return "-";
  }

  if (value < 1024) {
    return `${value} B`;
  }

  if (value < 1024 * 1024) {
    return `${Math.round(value / 1024)} KB`;
  }

  return `${(value / (1024 * 1024)).toFixed(1)} MB`;
};

const progressPercent = progress => {
  if (!progress || !Number.isFinite(progress.totalItems) || progress.totalItems <= 0) {
    return null;
  }

  const processed = Number.isFinite(progress.processedItems) ? progress.processedItems : 0;
  return Math.max(0, Math.min(100, Math.round((processed / progress.totalItems) * 100)));
};

const stageLabel = stage => ({
  queued: "Na fila",
  preparing: "Preparando",
  "code-intelligence": "Code intelligence",
  graphify: "Graphify",
  chunking: "Chunking",
  embeddings: "Embeddings",
  qdrant: "Qdrant",
  summary: "Resumo",
  completed: "Concluído",
  failed: "Falhou"
}[stage] ?? stage ?? "-");

const graphifyAssetUrl = relativePath => {
  const workspaceId = elements.workspaceId.value.trim() || "default";
  return `/workspaces/${encodeURIComponent(workspaceId)}/assets/${relativePath}`;
};

const escapeHtml = value => value
  .replaceAll("&", "&amp;")
  .replaceAll("<", "&lt;")
  .replaceAll(">", "&gt;");

const switchView = viewName => {
  state.activeView = viewName;
  for (const button of elements.viewTabs) {
    button.classList.toggle("is-active", button.dataset.view === viewName);
  }

  for (const panel of elements.viewPanels) {
    panel.classList.toggle("hidden", panel.id !== `view-${viewName}`);
  }

  if (viewName === "graphify") {
    refreshGraphify().catch(error => setResponse(error.message));
  }
};

const renderEmptyState = (target, message) => {
  target.innerHTML = `<div class="empty-state">${message}</div>`;
};

const refreshWorkspaces = async () => {
  const response = await fetch("/workspaces");
  state.workspaces = await response.json();

  elements.workspaces.replaceChildren();
  elements.workspaceCountBadge.textContent = String(state.workspaces.length);

  if (state.workspaces.length === 0) {
    const item = document.createElement("li");
    item.textContent = "No workspaces yet.";
    elements.workspaces.append(item);
    return;
  }

  for (const workspace of state.workspaces) {
    const item = document.createElement("li");
    item.classList.toggle("selected-item", workspace.id === elements.workspaceId.value.trim());

    const button = document.createElement("button");
    button.type = "button";
    button.textContent = workspace.id;
    button.addEventListener("click", () => {
      elements.workspaceId.value = workspace.id;
      loadWorkspace().catch(error => setResponse(error.message));
    });

    const meta = document.createElement("span");
    meta.className = "muted-text";
    meta.textContent = formatDateTime(workspace.createdAt);

    item.append(button);
    item.append(meta);
    elements.workspaces.append(item);
  }
};

const loadWorkspace = async () => {
  const workspaceId = elements.workspaceId.value.trim() || "default";
  elements.workspaceId.value = workspaceId;

  const [repositoriesResponse, documentsResponse, jobsResponse] = await Promise.all([
    fetch(`/workspaces/${encodeURIComponent(workspaceId)}/repositories`),
    fetch(`/workspaces/${encodeURIComponent(workspaceId)}/documents`),
    fetch(`/workspaces/${encodeURIComponent(workspaceId)}/jobs`)
  ]);

  state.repositories = await repositoriesResponse.json();
  state.documents = await documentsResponse.json();
  state.jobs = await jobsResponse.json();

  await refreshExplorer();
  await refreshGraphify();
  renderDashboard();
  renderResources();
  renderJobs();
  await refreshWorkspaces();
  ensureJobsPolling();
};

const renderDashboard = () => {
  const workspaceId = elements.workspaceId.value.trim() || "default";
  const lastJob = state.jobs[0];
  const queued = state.jobs.filter(job => job.status === "queued").length;
  const running = state.jobs.filter(job => job.status === "running").length;
  const failed = state.jobs.filter(job => job.status === "failed").length;
  const completed = state.jobs.filter(job => job.status === "completed").length;

  elements.heroWorkspaceTitle.textContent = `Workspace ${workspaceId}`;
  elements.heroWorkspaceSubtitle.textContent = state.explorer?.currentPath === ""
    ? "Raiz do workspace carregada. Use o explorer para navegar e o monitor para acompanhar jobs."
    : `Explorando ${state.explorer.currentPath}`;

  elements.metricRepositories.textContent = String(state.repositories.length);
  elements.metricDocuments.textContent = String(state.documents.length);
  elements.metricJobs.textContent = String(state.jobs.length);
  elements.metricLastJobStatus.textContent = lastJob?.status ?? "-";

  elements.repositoriesBadge.textContent = String(state.repositories.length);
  elements.documentsBadge.textContent = String(state.documents.length);

  const overviewCards = [
    { label: "Queued", value: queued, tone: "queued" },
    { label: "Running", value: running, tone: "running" },
    { label: "Completed", value: completed, tone: "completed" },
    { label: "Failed", value: failed, tone: "failed" }
  ];

  elements.overviewCards.innerHTML = overviewCards.map(card => `
    <article class="metric-card">
      <span class="metric-label">${card.label}</span>
      <strong>${card.value}</strong>
    </article>
  `).join("");

  if (state.jobs.length === 0) {
    renderEmptyState(elements.overviewJobs, "Nenhum job encontrado para este workspace.");
  } else {
    elements.overviewJobs.innerHTML = state.jobs.slice(0, 5).map(job => `
      <article>
        <div class="section-heading compact">
          <span class="status-pill ${job.status}">${job.status}</span>
          <span class="muted-text">${formatDateTime(job.createdAt)}</span>
        </div>
        <strong>${job.reason}</strong>
        <span class="mono-inline">${job.sourcePath}</span>
        ${job.error ? `<span class="muted-text">${escapeHtml(job.error)}</span>` : ""}
      </article>
    `).join("");
  }

  const rootEntries = state.explorer?.entries ?? [];
  if (rootEntries.length === 0) {
    renderEmptyState(elements.overviewRootEntries, "Nenhuma entrada encontrada na pasta atual.");
  } else {
    elements.overviewRootEntries.innerHTML = rootEntries.slice(0, 8).map(entry => `
      <article>
        <strong>${entry.name}</strong>
        <span class="muted-text">${entry.entryType === "directory" ? "Diretório" : formatSize(entry.sizeBytes)}</span>
        <span class="mono-inline">${entry.relativePath || "workspace/"}</span>
      </article>
    `).join("");
  }
};

const renderResources = () => {
  renderList(elements.repositories, state.repositories, repository => `
    <strong>${repository.name}</strong>
    <span class="mono-inline">${repository.relativePath}</span>
    <span class="muted-text">${repository.branch ?? "sem branch"}${repository.remoteUrl ? ` · ${repository.remoteUrl}` : ""}</span>
  `);

  renderList(elements.documents, state.documents, document => `
    <strong>${document.fileName}</strong>
    <span class="mono-inline">${document.relativePath}</span>
    <span class="muted-text">${document.category} · ${formatSize(document.sizeBytes)} · ${formatDateTime(document.lastModifiedAt)}</span>
  `);
};

const renderList = (target, items, label) => {
  target.replaceChildren();
  if (!Array.isArray(items) || items.length === 0) {
    const item = document.createElement("li");
    item.textContent = "None.";
    target.append(item);
    return;
  }

  for (const value of items) {
    const item = document.createElement("li");
    item.innerHTML = label(value);
    target.append(item);
  }
};

const filteredJobs = () => {
  const statusFilter = elements.jobStatusFilter.value;
  const textFilter = elements.jobSearchFilter.value.trim().toLowerCase();

  return state.jobs.filter(job => {
    const statusMatches = statusFilter === "all" || job.status === statusFilter;
    const haystack = `${job.reason} ${job.sourcePath} ${job.error ?? ""}`.toLowerCase();
    const textMatches = textFilter === "" || haystack.includes(textFilter);
    return statusMatches && textMatches;
  });
};

const renderJobs = () => {
  const jobs = filteredJobs();
  const runningJobs = state.jobs.filter(job => job.status === "running" || job.status === "queued");
  const pageSize = Number.parseInt(elements.jobPageSize.value, 10) || 10;
  const totalPages = Math.max(1, Math.ceil(jobs.length / pageSize));
  state.jobPage = Math.min(state.jobPage, totalPages);

  const start = (state.jobPage - 1) * pageSize;
  const currentPageItems = jobs.slice(start, start + pageSize);
  elements.jobsTableBody.replaceChildren();

  if (currentPageItems.length === 0) {
    const row = document.createElement("tr");
    row.innerHTML = '<td colspan="8">Nenhum job encontrado com os filtros atuais.</td>';
    elements.jobsTableBody.append(row);
  }

  for (const job of currentPageItems) {
    const percent = progressPercent(job.progress);
    const progressDetails = job.progress
      ? `
        <div class="progress-meta">
          <strong>${job.progress.processedItems ?? 0}${Number.isFinite(job.progress.totalItems) ? ` / ${job.progress.totalItems}` : ""}</strong>
          ${percent !== null ? `<div class="progress-bar"><span style="width:${percent}%"></span></div>` : ""}
          <span class="muted-text">${escapeHtml(job.progress.message ?? "")}</span>
        </div>
      `
      : "-";

    const row = document.createElement("tr");
    row.innerHTML = `
      <td><span class="status-pill ${job.status}">${job.status}</span></td>
      <td>${escapeHtml(stageLabel(job.progress?.stage))}</td>
      <td>${progressDetails}</td>
      <td><span class="mono-inline">${escapeHtml(job.progress?.currentPath ?? "-")}</span></td>
      <td>${escapeHtml(job.reason)}</td>
      <td><span class="mono-inline">${escapeHtml(job.sourcePath)}</span></td>
      <td>${formatDateTime(job.createdAt)}</td>
      <td>${job.error ? escapeHtml(job.error) : "-"}</td>
    `;
    row.style.cursor = "pointer";
    row.addEventListener("click", () => {
      state.selectedJobId = job.id;
      switchView("jobs");
      renderJobLogs();
      elements.jobLogsSelector.value = job.id;
      elements.jobLogsSelector.scrollIntoView({ behavior: "smooth", block: "center" });
    });
    elements.jobsTableBody.append(row);
  }

  elements.jobsPaginationSummary.textContent = `${jobs.length} resultado(s)`;
  elements.jobsPageIndicator.textContent = `Página ${state.jobPage} de ${totalPages}`;
  elements.jobsPrevPage.disabled = state.jobPage <= 1;
  elements.jobsNextPage.disabled = state.jobPage >= totalPages;

  elements.runningIndexingBadge.textContent = String(runningJobs.length);
  if (runningJobs.length === 0) {
    const latestJob = state.jobs[0];
    renderEmptyState(
      elements.runningIndexingList,
      latestJob
        ? `Nenhuma indexação em andamento. Último job: ${latestJob.reason} em ${formatDateTime(latestJob.createdAt)}.`
        : "Nenhuma indexação em andamento.");
  } else {
    elements.runningIndexingList.innerHTML = runningJobs.map(job => {
      const percent = progressPercent(job.progress);
      const pendingPaths = job.progress?.pendingPaths ?? [];
      return `
        <article>
          <div class="section-heading compact">
            <span class="status-pill ${job.status}">${job.status}</span>
            <span class="muted-text">${stageLabel(job.progress?.stage)}</span>
          </div>
          <strong>${escapeHtml(job.progress?.message ?? job.reason)}</strong>
          <span class="mono-inline">${escapeHtml(job.progress?.currentPath ?? job.sourcePath)}</span>
          ${percent !== null ? `<div class="progress-bar"><span style="width:${percent}%"></span></div>` : ""}
          <span class="muted-text">${job.progress?.processedItems ?? 0}${Number.isFinite(job.progress?.totalItems) ? ` de ${job.progress.totalItems}` : ""}</span>
          ${pendingPaths.length > 0 ? `<span class="muted-text">Próximos: ${escapeHtml(pendingPaths.join(", "))}</span>` : ""}
        </article>
      `;
    }).join("");
  }

  renderJobLogs();
};

const renderJobLogs = () => {
  const jobsWithLogs = state.jobs.filter(job => Array.isArray(job.logs) && job.logs.length > 0);
  elements.jobLogsBadge.textContent = String(jobsWithLogs.length);

  elements.jobLogsSelector.replaceChildren();
  if (jobsWithLogs.length === 0) {
    const option = document.createElement("option");
    option.value = "";
    option.textContent = "Nenhum job com logs";
    elements.jobLogsSelector.append(option);
    elements.jobLogsSelector.disabled = true;
    elements.jobLogsSummary.textContent = "Ainda não há timeline de execução disponível para este workspace.";
    renderEmptyState(elements.jobLogsList, "Os logs detalhados aparecerão conforme os jobs forem executados.");
    return;
  }

  elements.jobLogsSelector.disabled = false;
  if (!state.selectedJobId || !jobsWithLogs.some(job => job.id === state.selectedJobId)) {
    state.selectedJobId = jobsWithLogs[0].id;
  }

  for (const job of jobsWithLogs) {
    const option = document.createElement("option");
    option.value = job.id;
    option.textContent = `${job.reason} · ${formatDateTime(job.createdAt)}`;
    option.selected = job.id === state.selectedJobId;
    elements.jobLogsSelector.append(option);
  }

  const selectedJob = jobsWithLogs.find(job => job.id === state.selectedJobId) ?? jobsWithLogs[0];
  state.selectedJobId = selectedJob.id;
  elements.jobLogsSummary.textContent = `${selectedJob.logs.length} evento(s) registrados para ${selectedJob.reason}.`;

  elements.jobLogsList.innerHTML = selectedJob.logs
    .slice()
    .reverse()
    .map(log => {
      const percent = Number.isFinite(log.totalItems) && log.totalItems > 0 && Number.isFinite(log.processedItems)
        ? Math.max(0, Math.min(100, Math.round((log.processedItems / log.totalItems) * 100)))
        : null;

      return `
        <article class="log-entry">
          <div class="log-entry-header">
            <div class="section-heading compact">
              <span class="log-level ${escapeHtml(log.level)}">${escapeHtml(log.level)}</span>
              <strong>${escapeHtml(stageLabel(log.stage))}</strong>
            </div>
            <span class="muted-text">${formatDateTime(log.timestamp)}</span>
          </div>
          <span>${escapeHtml(log.message)}</span>
          ${log.currentPath ? `<span class="mono-inline">${escapeHtml(log.currentPath)}</span>` : ""}
          ${percent !== null ? `<div class="progress-bar"><span style="width:${percent}%"></span></div>` : ""}
          ${Number.isFinite(log.totalItems) ? `<span class="muted-text">${log.processedItems ?? 0} de ${log.totalItems}</span>` : ""}
          ${Array.isArray(log.pendingPaths) && log.pendingPaths.length > 0 ? `<span class="muted-text">Pendentes: ${escapeHtml(log.pendingPaths.join(", "))}</span>` : ""}
        </article>
      `;
    })
    .join("");
};

const refreshExplorer = async (nextPath = state.explorerPath) => {
  const workspaceId = elements.workspaceId.value.trim() || "default";
  state.explorerPath = nextPath ?? "";

  const query = new URLSearchParams();
  if (state.explorerPath) {
    query.set("path", state.explorerPath);
  }

  query.set("includeHidden", String(state.includeHiddenEntries));

  const response = await fetch(`/workspaces/${encodeURIComponent(workspaceId)}/files?${query.toString()}`);
  state.explorer = await response.json();
  renderExplorer();
};

const renderExplorer = () => {
  const explorer = state.explorer;
  if (!explorer) {
    renderEmptyState(elements.explorerEntries, "Explorer ainda não carregado.");
    return;
  }

  elements.explorerCurrentPath.textContent = explorer.currentPath ? `workspace/${explorer.currentPath}` : "workspace/";
  elements.explorerEntryCount.textContent = `${explorer.entries.length} item(ns)`;
  elements.explorerUp.disabled = explorer.parentPath === null;

  elements.explorerBreadcrumbs.replaceChildren();
  for (const crumb of explorer.breadcrumbs) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "secondary-button";
    button.textContent = crumb.name;
    button.addEventListener("click", () => {
      refreshExplorer(crumb.relativePath).catch(error => setResponse(error.message));
    });
    elements.explorerBreadcrumbs.append(button);
  }

  if (explorer.entries.length === 0) {
    renderEmptyState(elements.explorerEntries, "Esta pasta está vazia.");
    return;
  }

  elements.explorerEntries.replaceChildren();
  for (const entry of explorer.entries) {
    const article = document.createElement("article");
    article.className = "explorer-entry";

    const action = document.createElement("button");
    action.type = "button";
    action.innerHTML = `
      <span class="entry-title">
        <strong>${entry.name}</strong>
        <span class="mono-inline">${entry.relativePath || "workspace/"}</span>
      </span>
    `;

    action.addEventListener("click", () => {
      if (entry.entryType === "directory") {
        refreshExplorer(entry.relativePath).catch(error => setResponse(error.message));
        return;
      }

      openFilePreview(entry.relativePath).catch(error => setResponse(error.message));
    });

    const meta = document.createElement("div");
    meta.className = "entry-meta";
    meta.textContent = entry.entryType === "directory"
      ? `Diretório · ${formatDateTime(entry.lastModifiedAt)}`
      : `${formatSize(entry.sizeBytes)} · ${formatDateTime(entry.lastModifiedAt)}`;

    article.append(action);
    article.append(meta);
    elements.explorerEntries.append(article);
  }
};

const openFilePreview = async relativePath => {
  const workspaceId = elements.workspaceId.value.trim() || "default";
  const query = new URLSearchParams({ path: relativePath });
  const response = await fetch(`/workspaces/${encodeURIComponent(workspaceId)}/files/content?${query.toString()}`);
  state.preview = await response.json();

  elements.previewTitle.textContent = state.preview.relativePath;
  elements.previewMeta.textContent = `${formatSize(state.preview.sizeBytes)} · ${formatDateTime(state.preview.lastModifiedAt)}`;

  if (!state.preview.previewSupported) {
    elements.filePreview.textContent = "Preview indisponível para este tipo de arquivo.";
    return;
  }

  const suffix = state.preview.truncated ? "\n\n[preview truncado]" : "";
  elements.filePreview.textContent = `${state.preview.content ?? ""}${suffix}`;
};

const graphifyUrl = workspaceId => `/workspaces/${encodeURIComponent(workspaceId)}/graphify/index.html`;

const refreshGraphify = async () => {
  const workspaceId = elements.workspaceId.value.trim() || "default";
  const response = await fetch(`/workspaces/${encodeURIComponent(workspaceId)}/graphify/sources`);
  state.graphify = await response.json();

  const sources = state.graphify.sources ?? [];
  const preferred = state.graphify.preferredAssetPath;
  if (!state.graphifySelectedPath || !sources.some(source => source.relativePath === state.graphifySelectedPath)) {
    state.graphifySelectedPath = preferred ?? sources[0]?.relativePath ?? null;
  }

  elements.openGraphifyExternal.href = state.graphifySelectedPath
    ? graphifyAssetUrl(state.graphifySelectedPath)
    : graphifyUrl(workspaceId);

  renderGraphify();
};

const renderGraphify = () => {
  const graphify = state.graphify;
  if (!graphify) {
    elements.graphifyStatusBadge.textContent = "Indisponível";
    elements.graphifyStatusText.textContent = "Status do Graphify ainda não carregado.";
    elements.graphifyEmptyState.classList.remove("hidden");
    elements.graphifyFrame.classList.add("hidden");
    return;
  }

  const sources = graphify.sources ?? [];
  elements.graphifySourceSelector.replaceChildren();
  if (sources.length === 0) {
    const option = document.createElement("option");
    option.value = "";
    option.textContent = "Nenhuma fonte HTML encontrada";
    elements.graphifySourceSelector.append(option);
    elements.graphifySourceSelector.disabled = true;
  } else {
    elements.graphifySourceSelector.disabled = false;
    for (const source of sources) {
      const option = document.createElement("option");
      option.value = source.relativePath;
      option.textContent = `${source.label} · ${source.relativePath}`;
      option.selected = source.relativePath === state.graphifySelectedPath;
      elements.graphifySourceSelector.append(option);
    }
  }

  elements.graphifyGenerator.textContent = graphify.manifest?.generator ?? "desconhecido";
  elements.graphifyCounts.textContent = graphify.manifest?.nodeCount || graphify.manifest?.edgeCount
    ? `${graphify.manifest?.nodeCount ?? 0} nodes · ${graphify.manifest?.edgeCount ?? 0} edges`
    : "-";
  elements.graphifyCommand.textContent = graphify.processLog?.command ?? "-";
  elements.graphifyArguments.textContent = graphify.processLog?.arguments ?? "-";
  elements.graphifyProcessMeta.textContent = graphify.processLog?.exitCode !== null && graphify.processLog?.exitCode !== undefined
    ? `exit code ${graphify.processLog.exitCode}${graphify.processLog.startedAt ? ` · iniciado em ${formatDateTime(graphify.processLog.startedAt)}` : ""}${graphify.processLog.finishedAt ? ` · finalizado em ${formatDateTime(graphify.processLog.finishedAt)}` : ""}`
    : "Sem log de processo.";
  elements.graphifyProcessError.textContent = graphify.processLog?.error || graphify.processLog?.stderr || graphify.statusMessage || "-";
  elements.graphifyStderr.textContent = graphify.processLog?.stderr ?? "-";
  elements.graphifyStdout.textContent = graphify.processLog?.stdout ?? "-";

  if (graphify.hasVisualization && state.graphifySelectedPath) {
    const selectedSource = sources.find(source => source.relativePath === state.graphifySelectedPath);
    const isFallback = graphify.manifest?.generator === "fallback-file-graph"
      && state.graphifySelectedPath.startsWith("graphs/");

    elements.graphifyStatusBadge.textContent = isFallback ? "Fallback" : "Disponível";
    elements.graphifyStatusText.textContent = selectedSource
      ? `${graphify.statusMessage} Fonte atual: ${selectedSource.relativePath}.`
      : graphify.statusMessage;
    elements.graphifyEmptyState.classList.add("hidden");
    elements.graphifyFrame.classList.remove("hidden");
    const targetUrl = graphifyAssetUrl(state.graphifySelectedPath);
    if (elements.graphifyFrame.src !== new URL(targetUrl, window.location.origin).toString()) {
      elements.graphifyFrame.src = targetUrl;
    }
    return;
  }

  elements.graphifyStatusBadge.textContent = "Pendente";
  elements.graphifyStatusText.textContent = graphify.statusMessage;
  elements.graphifyEmptyState.classList.remove("hidden");
  elements.graphifyFrame.classList.add("hidden");
  elements.graphifyFrame.removeAttribute("src");
};

const refreshJobState = async () => {
  const workspaceId = elements.workspaceId.value.trim() || "default";
  const jobsResponse = await fetch(`/workspaces/${encodeURIComponent(workspaceId)}/jobs`);
  state.jobs = await jobsResponse.json();
  renderDashboard();
  renderJobs();

  if (state.activeView === "graphify") {
    await refreshGraphify();
  }
};

const ensureJobsPolling = () => {
  if (jobsPollingHandle !== null) {
    return;
  }

  jobsPollingHandle = window.setInterval(() => {
    refreshJobState().catch(() => {});
  }, 3000);
};

const currentArguments = () => {
  const maxResults = Number.parseInt(elements.maxResults.value, 10);
  const args = {
    workspaceId: elements.workspaceId.value.trim() || "default",
    query: elements.query.value.trim(),
    maxResults: Number.isFinite(maxResults) ? maxResults : 8
  };

  if (elements.toolName.value === "compare_business_rule_with_code") {
    args.businessRule = elements.businessRule.value.trim();
    args.codeQuery = elements.codeQuery.value.trim();
  }

  if (elements.toolName.value === "get_service_summary") {
    args.service = args.query;
  }

  if (elements.toolName.value === "explain_flow") {
    args.flow = args.query;
  }

  return args;
};

document.querySelector("#initialize").addEventListener("click", async () => {
  setResponse("Initializing...");
  setResponse(await request("initialize"));
});

document.querySelector("#send").addEventListener("click", async () => {
  setResponse("Running tool...");
  const result = await request("tools/call", {
    name: elements.toolName.value,
    arguments: currentArguments()
  });

  const text = result.content?.map(item => item.text).join("\n\n") ?? result;
  setResponse(text);
});

document.querySelector("#refreshWorkspaces").addEventListener("click", () => {
  Promise.all([refreshWorkspaces(), loadWorkspace()])
    .catch(error => setResponse(error.message));
});

elements.openWorkspace.addEventListener("click", () => {
  loadWorkspace().catch(error => setResponse(error.message));
});

document.querySelector("#createWorkspace").addEventListener("click", async () => {
  const workspaceId = elements.newWorkspaceId.value.trim();
  if (!workspaceId) {
    setResponse("Workspace id is required.");
    return;
  }

  const response = await fetch(`/workspaces/${encodeURIComponent(workspaceId)}`, {
    method: "POST"
  });
  const payload = await response.json();
  elements.workspaceId.value = payload.id;
  setResponse(payload);
  await refreshWorkspaces();
  await loadWorkspace();
});

document.querySelector("#registerRepository").addEventListener("click", async () => {
  const workspaceId = elements.workspaceId.value.trim() || "default";
  const name = elements.repositoryName.value.trim();
  if (!name) {
    setResponse("Repository name is required.");
    return;
  }

  const response = await fetch(`/workspaces/${encodeURIComponent(workspaceId)}/repositories`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      name,
      relativePath: elements.repositoryPath.value.trim() || undefined,
      remoteUrl: elements.repositoryRemote.value.trim() || undefined,
      branch: elements.repositoryBranch.value.trim() || undefined
    })
  });
  const payload = await response.json();
  setResponse(payload);
  await loadWorkspace();
});

document.querySelector("#uploadDocument").addEventListener("click", async () => {
  const workspaceId = elements.workspaceId.value.trim() || "default";
  const file = elements.documentFile.files[0];
  if (!file) {
    setResponse("Choose a document file first.");
    return;
  }

  const form = new FormData();
  form.append("file", file);

  const category = elements.documentCategory.value.trim() || "raw";
  const response = await fetch(
    `/workspaces/${encodeURIComponent(workspaceId)}/documents?category=${encodeURIComponent(category)}`,
    {
      method: "POST",
      body: form
    });

  const payload = await response.json();
  setResponse(payload);
  await loadWorkspace();
});

document.querySelector("#reindexWorkspace").addEventListener("click", async () => {
  const workspaceId = elements.workspaceId.value.trim() || "default";
  const response = await fetch(`/workspaces/${encodeURIComponent(workspaceId)}/jobs/reindex`, {
    method: "POST"
  });
  const payload = await response.json();
  setResponse(payload);
  await loadWorkspace();
});

elements.toolName.addEventListener("change", () => {
  elements.compareFields.classList.toggle(
    "hidden",
    elements.toolName.value !== "compare_business_rule_with_code"
  );
});

elements.workspaceId.addEventListener("change", () => {
  loadWorkspace().catch(error => setResponse(error.message));
});

for (const button of elements.viewTabs) {
  button.addEventListener("click", () => {
    switchView(button.dataset.view);
  });
}

elements.openJobsView.addEventListener("click", () => switchView("jobs"));
elements.openExplorerRoot.addEventListener("click", async () => {
  switchView("explorer");
  await refreshExplorer("");
});

elements.jobStatusFilter.addEventListener("change", () => {
  state.jobPage = 1;
  renderJobs();
});

elements.jobSearchFilter.addEventListener("input", () => {
  state.jobPage = 1;
  renderJobs();
});

elements.jobPageSize.addEventListener("change", () => {
  state.jobPage = 1;
  renderJobs();
});

elements.jobsPrevPage.addEventListener("click", () => {
  state.jobPage = Math.max(1, state.jobPage - 1);
  renderJobs();
});

elements.jobsNextPage.addEventListener("click", () => {
  state.jobPage += 1;
  renderJobs();
});

elements.jobLogsSelector.addEventListener("change", () => {
  state.selectedJobId = elements.jobLogsSelector.value || null;
  renderJobLogs();
});

elements.includeHiddenEntries.addEventListener("change", () => {
  state.includeHiddenEntries = elements.includeHiddenEntries.checked;
  refreshExplorer().catch(error => setResponse(error.message));
});

elements.explorerUp.addEventListener("click", () => {
  const parentPath = state.explorer?.parentPath;
  if (parentPath === null || parentPath === undefined) {
    return;
  }

  refreshExplorer(parentPath).catch(error => setResponse(error.message));
});

elements.refreshExplorer.addEventListener("click", () => {
  refreshExplorer().catch(error => setResponse(error.message));
});

elements.refreshGraphify.addEventListener("click", () => {
  refreshGraphify().catch(error => setResponse(error.message));
});

elements.graphifySourceSelector.addEventListener("change", () => {
  state.graphifySelectedPath = elements.graphifySourceSelector.value || null;
  renderGraphify();
});

switchView("overview");

Promise.all([refreshWorkspaces(), loadWorkspace()])
  .catch(error => setResponse(error.message));
