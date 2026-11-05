<script lang="ts">
	import { resolve } from '$app/paths';
	import Icon from './Icon.svelte';
</script>

<div class="text-center py-5">
	<Icon name="not-allowed" class="display-4 text-body-secondary d-block mb-3" />
	<h1 class="h3">Not allowed</h1>
	<p class="text-body-secondary">This page is for Admins only.</p>
	<a class="btn btn-primary" href={resolve('/')}>Go to the Library</a>
</div>
