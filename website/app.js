const RELEASE_FALLBACK = {
  tag: "v1.4.0",
  version: "1.4.0",
  publishedAt: "2026-08-01T06:46:15Z",
  assetName: "LazyForza-1.4.0-win-x64.zip",
};

const RELEASE_SOURCES = {
  github: {
    api: "https://api.github.com/repos/Laz22y/LazyForza/releases/latest",
    button: "#github-download",
  },
  gitcode: {
    api: "https://api.gitcode.com/api/v5/repos/Laz22y/LazyForza/releases/latest",
    button: "#gitcode-download",
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

function releaseAsset(release, version) {
  const expected = `LazyForza-${version}-win-x64.zip`;
  return release?.assets?.find((asset) => asset.name === expected) || null;
}

function normalizeRelease(release, source) {
  const version = stableVersion(release?.tag_name);
  const asset = version ? releaseAsset(release, version) : null;
  if (!version || !asset) {
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
    assetName: asset.name,
    downloadUrl:
      source === "github" && asset.browser_download_url
        ? asset.browser_download_url
        : null,
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
  if (Number.isNaN(date.getTime())) {
    return {
      iso: "2026-07-29",
      label: "2026.07.29 更新",
    };
  }

  const parts = new Intl.DateTimeFormat("zh-CN", {
    timeZone: "Asia/Shanghai",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).formatToParts(date);
  const values = Object.fromEntries(parts.map((part) => [part.type, part.value]));

  return {
    iso: `${values.year}-${values.month}-${values.day}`,
    label: `${values.year}.${values.month}.${values.day} 更新`,
  };
}

function downloadUrl(source, release) {
  const encodedTag = encodeURIComponent(release.tag);
  const encodedAsset = encodeURIComponent(release.assetName);
  if (source === "github") {
    return (
      release.downloadUrl ||
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
  if (!card) {
    return;
  }

  const date = formatDate(release.publishedAt);
  const version = card.querySelector("[data-release-version]");
  const time = card.querySelector("[data-release-date]");
  version.textContent = release.tag;
  time.textContent = date.label;
  time.dateTime = date.iso;
  card.href = downloadUrl(source, release);
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
