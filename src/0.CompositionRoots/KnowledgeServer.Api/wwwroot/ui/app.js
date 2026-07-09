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
  documentFile: document.querySelector("#documentFile")
};

const setResponse = value => {
  elements.response.textContent = typeof value === "string" ? value : JSON.stringify(value, null, 2);
};

const refreshWorkspaces = async () => {
  const response = await fetch("/workspaces");
  const workspaces = await response.json();

  elements.workspaces.replaceChildren();
  if (workspaces.length === 0) {
    const item = document.createElement("li");
    item.textContent = "No workspaces yet.";
    elements.workspaces.append(item);
    return;
  }

  for (const workspace of workspaces) {
    const item = document.createElement("li");
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = workspace.id;
    button.addEventListener("click", () => {
      elements.workspaceId.value = workspace.id;
      refreshWorkspaceDetails().catch(error => setResponse(error.message));
    });
    item.append(button);
    elements.workspaces.append(item);
  }
};

const refreshWorkspaceDetails = async () => {
  const workspaceId = elements.workspaceId.value.trim() || "default";
  const [repositoriesResponse, documentsResponse] = await Promise.all([
    fetch(`/workspaces/${encodeURIComponent(workspaceId)}/repositories`),
    fetch(`/workspaces/${encodeURIComponent(workspaceId)}/documents`)
  ]);

  const repositories = await repositoriesResponse.json();
  const documents = await documentsResponse.json();

  renderList(elements.repositories, repositories, repository =>
    `${repository.name} · ${repository.relativePath}${repository.branch ? ` · ${repository.branch}` : ""}`);
  renderList(elements.documents, documents, document =>
    `${document.relativePath} · ${Math.round(document.sizeBytes / 1024)} KB`);
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
    item.textContent = label(value);
    target.append(item);
  }
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
  Promise.all([refreshWorkspaces(), refreshWorkspaceDetails()])
    .catch(error => setResponse(error.message));
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
  await refreshWorkspaceDetails();
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
  await refreshWorkspaceDetails();
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
  await refreshWorkspaceDetails();
});

document.querySelector("#reindexWorkspace").addEventListener("click", async () => {
  const workspaceId = elements.workspaceId.value.trim() || "default";
  const response = await fetch(`/workspaces/${encodeURIComponent(workspaceId)}/jobs/reindex`, {
    method: "POST"
  });
  const payload = await response.json();
  setResponse(payload);
});

elements.toolName.addEventListener("change", () => {
  elements.compareFields.classList.toggle(
    "hidden",
    elements.toolName.value !== "compare_business_rule_with_code"
  );
});

elements.workspaceId.addEventListener("change", () => {
  refreshWorkspaceDetails().catch(error => setResponse(error.message));
});

Promise.all([refreshWorkspaces(), refreshWorkspaceDetails()])
  .catch(error => setResponse(error.message));
