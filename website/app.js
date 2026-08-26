const RELEASE_FALLBACK = {
  tag: "v1.5.0",
  version: "1.5.0",
  publishedAt: "2026-08-26T09:39:32Z",
  installerName: "LazyForza-1.5.0-win-x64-setup.exe",
  portableName: "LazyForza-1.5.0-win-x64.zip",
};

const RELEASE_SOURCES = {
  github: {
    api: "https://api.github.com/repos/Laz22y/LazyForza/releases/latest",
    button: "#github-download",
    portable: "#github-portable",
  },
  gitcode: {
    api: "https://api.gitcode.com/api/v5/repos/Laz22y/LazyForza/releases/latest",
    button: "#gitcode-download",
    portable: "#gitcode-portable",
  },
};

function stableVersion(tag) {
  const match = String(tag || "").match(/^v?(\d+\.\d+\.\d+)$/);
  return match ? match[1] : null;
}

function compareVersions(left, right) {
  const leftParts = left.split(".").map(Number);
  const rightParts = right.split(".").map(Number);
  for (let index = 0; index < 3; index += 1) {
    if (leftParts[index] !== rightParts[index]) {
      return leftParts[index] - rightParts[index];
    }
  }
  return 0;
}

function releaseAsset(release, expected) {
  return release?.assets?.find((asset) => asset.name === expected) || null;
}

function normalizeRelease(release, source) {
  const version = stableVersion(release?.tag_name);
  const installerName = version
    ? `LazyForza-${version}-win-x64-setup.exe`
    : null;
  const portableName = version ? `LazyForza-${version}-win-x64.zip` : null;
  const installer = installerName ? releaseAsset(release, installerName) : null;
  const portable = portableName ? releaseAsset(release, portableName) : null;
  if (!version || !installer || !portable) {
    return null;
  }

  return {
    source,
    tag: `v${version}`,
    version,
    publishedAt:
      release.published_at ||
      release.created_at ||
      RELEASE_FALLBACK.publishedAt,
    installerName: installer.name,
    installerUrl: source === "github" ? installer.browser_download_url : null,
    portableName: portable.name,
    portableUrl: source === "github" ? portable.browser_download_url : null,
  };
}

async function fetchJson(url) {
  const controller = new AbortController();
  const timeout = window.setTimeout(() => controller.abort(), 6500);

  try {
    const response = await fetch(url, {
      cache: "no-store",
      headers: { Accept: "application/json" },
      signal: controller.signal,
    });
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }
    return await response.json();
  } finally {
    window.clearTimeout(timeout);
  }
}

function formatDate(value) {
  const date = new Date(value);
  const english = window.WebsiteI18n?.language === "en";
  if (Number.isNaN(date.getTime())) {
    return {
      iso: "2026-07-29",
      label: english ? "Updated Jul 29, 2026" : "2026.07.29 更新",
    };
  }

  const parts = new Intl.DateTimeFormat(english ? "en-US" : "zh-CN", {
    timeZone: "Asia/Shanghai",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).formatToParts(date);
  const values = Object.fromEntries(parts.map((part) => [part.type, part.value]));

  return {
    iso: `${values.year}-${values.month}-${values.day}`,
    label: english
      ? `Updated ${values.month}/${values.day}/${values.year}`
      : `${values.year}.${values.month}.${values.day} 更新`,
  };
}

function downloadUrl(source, release, kind) {
  const encodedTag = encodeURIComponent(release.tag);
  const assetName = kind === "installer"
    ? release.installerName
    : release.portableName;
  const directUrl = kind === "installer"
    ? release.installerUrl
    : release.portableUrl;
  const encodedAsset = encodeURIComponent(assetName);
  if (source === "github") {
    return (
      directUrl ||
      `https://github.com/Laz22y/LazyForza/releases/download/${encodedTag}/${encodedAsset}`
    );
  }

  return (
    `https://api.gitcode.com/api/v5/repos/Laz22y/LazyForza/releases/${encodedTag}` +
    `/attach_files/${encodedAsset}/download`
  );
}

function renderCard(source, release) {
  const card = document.querySelector(RELEASE_SOURCES[source].button);
  const portable = document.querySelector(RELEASE_SOURCES[source].portable);
  if (!card || !portable) {
    return;
  }

  const date = formatDate(release.publishedAt);
  const version = card.querySelector("[data-release-version]");
  const time = card.querySelector("[data-release-date]");
  version.textContent = release.tag;
  time.textContent = date.label;
  time.dateTime = date.iso;
  card.href = downloadUrl(source, release, "installer");
  portable.href = downloadUrl(source, release, "portable");
  card.dataset.releaseReady = "true";
}

function renderRelease(release) {
  renderCard("gitcode", release);
  renderCard("github", release);
}

async function refreshRelease() {
  const sourceNames = Object.keys(RELEASE_SOURCES);
  const results = await Promise.allSettled(
    sourceNames.map((source) => fetchJson(RELEASE_SOURCES[source].api)),
  );
  const releases = results
    .map((result, index) =>
      result.status === "fulfilled"
        ? normalizeRelease(result.value, sourceNames[index])
        : null,
    )
    .filter(Boolean);

  if (releases.length === 0) {
    return;
  }

  const latest = releases.sort((left, right) =>
    compareVersions(right.version, left.version),
  )[0];

  for (const source of sourceNames) {
    const matchingSource = releases.find(
      (release) =>
        release.source === source && release.version === latest.version,
    );
    renderCard(source, matchingSource || latest);
  }
}

renderRelease(RELEASE_FALLBACK);
refreshRelease().catch(() => {});
document.querySelector("#copyright-year").textContent =
  String(new Date().getFullYear());
