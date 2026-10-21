/* Scout Wiki - shared behaviour: active link, mobile nav, sidebar filter. */
(function () {
  "use strict";

  // Mark the nav link matching the current document as active.
  function markActive() {
    var here = location.pathname.split("/").pop() || "index.html";
    var parts = location.pathname.split("/");
    var parentDir = parts[parts.length - 2] || "";
    var links = document.querySelectorAll("nav#nav a");
    var matched = null;
    links.forEach(function (a) {
      var href = a.getAttribute("href") || "";
      var clean = href.split("#")[0];
      if (!clean) return;
      var segs = clean.split("/").filter(function (s) { return s !== ".."; });
      var file = segs[segs.length - 1];
      var dir = segs.length > 1 ? segs[segs.length - 2] : "";
      if (file === here && (dir === parentDir || dir === "")) {
        matched = a;
      }
    });
    if (matched) {
      matched.classList.add("active");
      matched.scrollIntoView({ block: "nearest" });
    }
  }

  // Mobile sidebar toggle.
  function wireToggle() {
    var btn = document.getElementById("menuToggle");
    var sb = document.getElementById("sidebar");
    if (!btn || !sb) return;
    btn.addEventListener("click", function () { sb.classList.toggle("open"); });
    sb.addEventListener("click", function (e) {
      if (e.target.tagName === "A") sb.classList.remove("open");
    });
  }

  // Client-side filter over the catalog nav.
  function wireFilter() {
    var input = document.getElementById("navFilter");
    if (!input) return;
    input.addEventListener("input", function () {
      var q = input.value.trim().toLowerCase();
      document.querySelectorAll("nav#nav li").forEach(function (li) {
        var a = li.querySelector("a");
        if (!a) return;
        var hit = a.textContent.toLowerCase().indexOf(q) !== -1;
        li.classList.toggle("nav-hidden", q.length > 0 && !hit);
      });
      document.querySelectorAll("nav#nav .nav-group-title").forEach(function (t) {
        var next = t.nextElementSibling, anyVisible = false;
        if (next) next.querySelectorAll("li").forEach(function (li) {
          if (!li.classList.contains("nav-hidden")) anyVisible = true;
        });
        t.classList.toggle("nav-hidden", q.length > 0 && !anyVisible);
      });
    });
  }

  document.addEventListener("DOMContentLoaded", function () {
    markActive();
    wireToggle();
    wireFilter();
  });
})();
