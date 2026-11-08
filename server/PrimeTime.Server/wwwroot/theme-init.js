// Loaded before the page is drawn, so a dark page never flashes light.
(function () {
  var theme = null;
  try { theme = localStorage.getItem("primetime.theme"); } catch (e) {}
  if (theme !== "light" && theme !== "dark") {
    theme = matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  }
  document.documentElement.dataset.theme = theme;
})();
