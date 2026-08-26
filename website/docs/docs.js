const sidebarLinks = [...document.querySelectorAll(".docs-sidebar nav a")];
const localizedText = (text) => window.WebsiteI18n?.text(text) || text;
const sections = sidebarLinks
  .map((link) => document.querySelector(link.getAttribute("href")))
  .filter(Boolean);

function setActiveSection(id) {
  for (const link of sidebarLinks) {
    const active = link.getAttribute("href") === `#${id}`;
    link.classList.toggle("is-active", active);
    if (active) link.setAttribute("aria-current", "location");
    else link.removeAttribute("aria-current");
  }
}

if ("IntersectionObserver" in window) {
  const observer = new IntersectionObserver(
    (entries) => {
      const visible = entries
        .filter((entry) => entry.isIntersecting)
        .sort((left, right) => left.boundingClientRect.top - right.boundingClientRect.top);
      if (visible[0]) setActiveSection(visible[0].target.id);
    },
    { rootMargin: "-18% 0px -68%", threshold: [0, 0.05] },
  );
  sections.forEach((section) => observer.observe(section));
} else if (sections[0]) {
  setActiveSection(sections[0].id);
}

for (const pre of document.querySelectorAll("pre")) {
  const code = pre.querySelector("code");
  if (!code) continue;
  const button = document.createElement("button");
  button.className = "copy-button";
  button.type = "button";
  button.textContent = localizedText("复制");
  button.setAttribute("aria-label", localizedText("复制代码"));
  button.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(code.textContent);
      button.textContent = localizedText("已复制");
      window.setTimeout(() => { button.textContent = localizedText("复制"); }, 1400);
    } catch {
      button.textContent = localizedText("复制失败");
    }
  });
  pre.append(button);
}

document.querySelector("#copyright-year").textContent = String(new Date().getFullYear());
