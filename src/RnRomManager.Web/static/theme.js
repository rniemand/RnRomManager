// Runs before first paint (blocking <script> in app.html). Stored choice: 'light' | 'dark' | 'auto' (default).
(function () {
	var stored = null;
	try {
		stored = localStorage.getItem('theme');
	} catch {
		// storage blocked: fall back to auto
	}
	var theme =
		stored === 'light' || stored === 'dark'
			? stored
			: window.matchMedia('(prefers-color-scheme: dark)').matches
				? 'dark'
				: 'light';
	document.documentElement.setAttribute('data-bs-theme', theme);
})();
