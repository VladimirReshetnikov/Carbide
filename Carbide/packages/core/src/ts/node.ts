// Node-only subpath export. Consumers in Node can opt into NodeHostAdapter and the exported
// asset-server primitive via `import { NodeHostAdapter } from "@carbide/core/node"`; the
// browser's static import graph does not see node:* built-ins as a result.

export {
    NodeHostAdapter,
    type NodeAdapterOptions,
    type NodeAdapterAssetDelivery,
} from "./host/node/node-adapter.js";

export {
    startAssetServer,
    type AssetServerHandle,
    type AssetServerOptions,
} from "./host/node/asset-server.js";
