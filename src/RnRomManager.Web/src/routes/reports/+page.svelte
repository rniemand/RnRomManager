<script lang="ts">
	import { onMount } from 'svelte';
	import { goto } from '$app/navigation';

	// eslint-disable-next-line svelte/no-navigation-without-resolve -- the route arrives with RP3
	onMount(() => void goto('/reports/library-size', { replace: true }));
</script>
