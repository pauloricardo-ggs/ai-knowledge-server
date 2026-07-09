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
  compareFields: document.querySelector("#compareFields")
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
    });
    item.append(button);
    elements.workspaces.append(item);
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
  refreshWorkspaces().catch(error => setResponse(error.message));
});

elements.toolName.addEventListener("change", () => {
  elements.compareFields.classList.toggle(
    "hidden",
    elements.toolName.value !== "compare_business_rule_with_code"
  );
});

refreshWorkspaces().catch(error => setResponse(error.message));
