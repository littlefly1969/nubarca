// Metro must be told about the shared contracts package.
//
// @nubarca/contracts lives at the repository root, OUTSIDE this app's folder.
// npm links it into node_modules through its `file:` dependency, which is what
// lets `node --test` load its TypeScript (Node resolves the symlink to a real
// path outside node_modules, where type stripping is allowed).
//
// Metro needs two further things: the folder in `watchFolders`, so an edit to
// a contract triggers a rebuild instead of serving a stale module, and symlink
// resolution, so the link is followed rather than treated as a dead end.
const path = require('path');
const { getDefaultConfig } = require('expo/metro-config');

const projectRoot = __dirname;
const contractsRoot = path.resolve(projectRoot, '..', 'packages', 'contracts');

const config = getDefaultConfig(projectRoot);
config.watchFolders = [...(config.watchFolders ?? []), contractsRoot];
config.resolver.unstable_enableSymlinks = true;
// Resolution still starts from THIS app's node_modules, so the shared package
// never brings a second copy of react/react-native into the bundle.
config.resolver.nodeModulesPaths = [path.resolve(projectRoot, 'node_modules')];

module.exports = config;
