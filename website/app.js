const RELEASE_FALLBACK = {
  tag: "v1.3.0",
  version: "1.3.0",
  publishedAt: "2026-07-28T07:19:54Z",
  assetName: "LazyForza-1.3.0-win-x64.zip",
};

const GITHUB_API =
  "https://api.github.com/repos/Laz22y/LazyForza/releases/latest";
const GITCODE_API =
  "https://api.gitcode.com/api/v5/repos/Laz22y/LazyForza/releases/latest";

function stableVersion(tag) {
  const match = String(tag || "").match(/^v?(\d+\.\d+\.\d+)$/);
  return match ? match[1] : null;
}

function releaseAsset(release, version) {
  const expected = `LazyForza-${version}-win-x64.zip`;
  return release?.assets?.find((asset) => asset.name === expected) || null;
}

async function fetchJson(url) {
  const controller = new AbortController();
  const timeout = window.setTimeout(() => controller.abort(), 6000);

  try {
    const response = await fetch(url, {
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
      iso: "2026-07-28",
      label: "2026.07.28 更新",
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

function renderRelease(release) {
  const date = formatDate(release.publishedAt);

  document.querySelectorAll("[data-release-version]").forEach((element) => {
    element.textContent = release.tag;
  });
  document.querySelectorAll("[data-release-date]").forEach((element) => {
    element.textContent = date.label;
    element.dateTime = date.iso;
  });

  const encodedTag = encodeURIComponent(release.tag);
  const encodedAsset = encodeURIComponent(release.assetName);
  document.querySelector("#gitcode-download").href =
    `https://api.gitcode.com/api/v5/repos/Laz22y/LazyForza/releases/${encodedTag}` +
    `/attach_files/${encodedAsset}/download`;
  document.querySelector("#github-download").href =
    `https://github.com/Laz22y/LazyForza/releases/download/${encodedTag}/${encodedAsset}`;
}

async function refreshRelease() {
  const [gitCodeResult, gitHubResult] = await Promise.allSettled([
    fetchJson(GITCODE_API),
    fetchJson(GITHUB_API),
  ]);

  const gitCode =
    gitCodeResult.status === "fulfilled" ? gitCodeResult.value : null;
  const gitHub =
    gitHubResult.status === "fulfilled" ? gitHubResult.value : null;
  const preferred = gitCode || gitHub;
  const version = stableVersion(preferred?.tag_name);
  const asset = version ? releaseAsset(preferred, version) : null;

  if (!version || !asset) {
    return;
  }

  const dateSource =
    gitHub?.tag_name === preferred.tag_name ? gitHub : preferred;
  renderRelease({
    tag: `v${version}`,
    version,
    publishedAt:
      dateSource?.published_at ||
      dateSource?.created_at ||
      RELEASE_FALLBACK.publishedAt,
    assetName: asset.name,
  });
}

renderRelease(RELEASE_FALLBACK);
refreshRelease().catch(() => {});
document.querySelector("#copyright-year").textContent =
  String(new Date().getFullYear());
