<script lang="ts">
	import { onMount } from 'svelte';
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';

	onMount(() => void goto(resolve('/reports/library-size'), { replace: true }));
</script>
