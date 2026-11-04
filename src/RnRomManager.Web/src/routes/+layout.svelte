<script lang="ts">
	import 'bootstrap/dist/css/bootstrap.min.css';
	import 'bootstrap-icons/font/bootstrap-icons.min.css';
	// Font Awesome is only for gaps in Bootstrap Icons, e.g. brand logos (spec §11).
	import '@fortawesome/fontawesome-free/css/fontawesome.min.css';
	import '@fortawesome/fontawesome-free/css/brands.min.css';
	import type { LayoutProps } from './$types';

	let { children }: LayoutProps = $props();
</script>

{@render children()}
